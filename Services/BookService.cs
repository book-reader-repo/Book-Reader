using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace BookViewer
{
    public class BookService
    {
        private string _currentBookPath;
        private string _currentBookUid;
        private List<string> _pageFiles = new();
        private int _currentPageIndex = -1;
        private readonly DecryptionService _decryptionService = new();
        private string _bookTitle = "Unknown Book";
        private string _currentPageHtml = "";
        private readonly Dictionary<string, string> _imageCache = new();
        private readonly Dictionary<string, string> _fontCache = new();
        private bool _sideBySideMode = false;
        private double _currentZoom = 1.0;
        private double _baseScale = 1.0;
        private string _tempFolder;
        
        // Track answer visibility state
        private bool _showTeacherNotes = false;
        private bool _showStudentAnswers = false;

        private static readonly object _logLock = new object();
        private static string _logFilePath = null;
        
        private string GetLogFilePath()
        {
            if (_logFilePath == null)
            {
                string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string logFolder = Path.Combine(documentsPath, "BookViewer");
                if (!Directory.Exists(logFolder))
                {
                    Directory.CreateDirectory(logFolder);
                }
                _logFilePath = Path.Combine(logFolder, $"BookService_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            }
            return _logFilePath;
        }

        private void Log(string message)
        {
            try
            {
                string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {message}";
                string logFile = GetLogFilePath();
                
                lock (_logLock)
                {
                    File.AppendAllText(logFile, logMessage + Environment.NewLine);
                }
                
                System.Diagnostics.Debug.WriteLine(logMessage);
                
                if (message.Length < 80)
                {
                    OnStatusChanged?.Invoke(this, message);
                }
                else
                {
                    OnStatusChanged?.Invoke(this, message.Substring(0, 77) + "...");
                }
            }
            catch
            {
                // Ignore logging errors
            }
        }

        public event EventHandler<List<string>> OnPagesLoaded;
        public event EventHandler<string> OnPageChanged;
        public event EventHandler<string> OnStatusChanged;
        public event EventHandler<string> OnBookLoaded;
        public event EventHandler<bool> OnToggleAnswersRequested;
        public event EventHandler<bool> OnSideBySideToggled;
        public event EventHandler<double> OnZoomChanged;

        public List<string> PageFiles => _pageFiles;
        public int CurrentPageIndex => _currentPageIndex;
        public string CurrentBookPath => _currentBookPath;
        public string BookTitle => _bookTitle;
        public string CurrentPageHtml => _currentPageHtml;
        public bool SideBySideMode => _sideBySideMode;
        public double CurrentZoom => _currentZoom;
        
        public bool ShowTeacherNotes => _showTeacherNotes;
        public bool ShowStudentAnswers => _showStudentAnswers;

        public void SetShowTeacherNotes(bool show)
        {
            _showTeacherNotes = show;
            Log($"Teacher notes set to: {show}");
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
            {
                _ = LoadPageAsync(_currentPageIndex);
            }
        }

        public void SetShowStudentAnswers(bool show)
        {
            _showStudentAnswers = show;
            Log($"Student answers set to: {show}");
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
            {
                _ = LoadPageAsync(_currentPageIndex);
            }
        }

        public bool IsFileEncrypted(string content)
        {
            return _decryptionService.IsEncrypted(content);
        }

        public string DecryptFile(string content, string fileName)
        {
            return _decryptionService.DecryptWithFileName(content, fileName);
        }

        public void SetZoom(double zoom)
        {
            _currentZoom = Math.Max(0.5, Math.Min(3.0, zoom));
            OnZoomChanged?.Invoke(this, _currentZoom);
            
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
            {
                _ = LoadPageAsync(_currentPageIndex);
            }
        }

        public void ZoomIn()
        {
            SetZoom(_currentZoom + 0.1);
        }

        public void ZoomOut()
        {
            SetZoom(_currentZoom - 0.1);
        }

        public void ResetZoom()
        {
            SetZoom(1.0);
        }

        public void ToggleSideBySide()
        {
            _sideBySideMode = !_sideBySideMode;
            OnSideBySideToggled?.Invoke(this, _sideBySideMode);
            
            if (_currentPageIndex >= 0 && _currentPageIndex < _pageFiles.Count)
            {
                _ = LoadPageAsync(_currentPageIndex);
            }
        }

        public async Task<bool> LoadBookAsync(string folderPath)
        {
            try
            {
                Log($"=== LOADING BOOK from: {folderPath} ===");
                _currentBookPath = folderPath;
                
                _tempFolder = Path.Combine(FileSystem.CacheDirectory, "BookViewer", $"temp_{Guid.NewGuid().ToString().Substring(0, 8)}");
                Directory.CreateDirectory(_tempFolder);
                Log($"Temp folder: {_tempFolder}");
                
                var bookXmlPath = Path.Combine(folderPath, "book.xml");

                if (!File.Exists(bookXmlPath))
                {
                    Log($"ERROR: book.xml not found at: {bookXmlPath}");
                    OnStatusChanged?.Invoke(this, "book.xml not found");
                    return false;
                }

                var folderName = Path.GetFileName(folderPath);
                var bookIdMatch = Regex.Match(folderName, @"book_(\d+)");
                if (bookIdMatch.Success)
                {
                    _currentBookUid = bookIdMatch.Groups[1].Value;
                    Log($"Extracted book ID from folder: {_currentBookUid}");
                }

                var content = await File.ReadAllTextAsync(bookXmlPath);
                Log($"book.xml loaded, size: {content.Length} bytes");

                if (string.IsNullOrEmpty(_currentBookUid))
                {
                    var uidMatch = Regex.Match(content, @"id=""([^""]+)""");
                    if (uidMatch.Success)
                    {
                        _currentBookUid = uidMatch.Groups[1].Value;
                        Log($"Extracted Book UID from book.xml: {_currentBookUid}");
                    }
                }

                bool bookXmlDecrypted = false;
                if (_decryptionService.IsEncrypted(content))
                {
                    Log("book.xml is encrypted, decrypting...");
                    
                    if (string.IsNullOrEmpty(_currentBookUid))
                    {
                        Log("ERROR: Could not find book UID for decryption");
                        OnStatusChanged?.Invoke(this, "Could not decrypt book.xml - UID not found");
                        return false;
                    }
                    
                    content = _decryptionService.DecryptBookFile(content, _currentBookUid);
                    Log($"book.xml decrypted, size: {content.Length} bytes");
                    await File.WriteAllTextAsync(bookXmlPath, content);
                    bookXmlDecrypted = true;
                }
                else
                {
                    Log("book.xml is already decrypted");
                }

                _bookTitle = "Unknown Book";
                var titleMatch = Regex.Match(content, @"name=""([^""]+)""");
                if (titleMatch.Success) _bookTitle = titleMatch.Groups[1].Value;
                Log($"Book title: {_bookTitle}");
                Log($"Book UID: {_currentBookUid}");

                OnBookLoaded?.Invoke(this, _bookTitle);

                // Only decrypt files if book.xml was encrypted or if files need decryption
                if (bookXmlDecrypted)
                {
                    await DecryptBookFilesAsync(folderPath);
                }
                else
                {
                    // Check if any HTML files are still encrypted
                    bool needsDecryption = false;
                    var sampleHtmlFiles = Directory.GetFiles(folderPath, "steps_*.html", SearchOption.AllDirectories).Take(3).ToList();
                    foreach (var sampleFile in sampleHtmlFiles)
                    {
                        try
                        {
                            var sampleContent = await File.ReadAllTextAsync(sampleFile);
                            if (!sampleContent.Contains("<") || !sampleContent.Contains(">") || 
                                !(sampleContent.Contains("</") || sampleContent.Contains("/>")))
                            {
                                needsDecryption = true;
                                break;
                            }
                            if (!sampleContent.Contains("class=") && !sampleContent.Contains("<div"))
                            {
                                needsDecryption = true;
                                break;
                            }
                        }
                        catch
                        {
                            needsDecryption = true;
                            break;
                        }
                    }

                    if (needsDecryption)
                    {
                        Log("Some files appear to still be encrypted, running decryption...");
                        await DecryptBookFilesAsync(folderPath);
                    }
                    else
                    {
                        Log("All files appear to be already decrypted, skipping decryption");
                    }
                }

                _pageFiles.Clear();

                var stepsFiles = Directory.GetFiles(folderPath, "steps_*.html", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(folderPath, "step_*.html", SearchOption.AllDirectories))
                    .Where(f => IsPureStepsFile(Path.GetFileName(f)))
                    .Distinct()
                    .ToList();

                Log($"Found {stepsFiles.Count} step files");

                _pageFiles = stepsFiles
                    .GroupBy(f => Path.GetDirectoryName(f) ?? "")
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(g =>
                        g.Select(f => new { Path = f, Index = ParseStepIndex(Path.GetFileName(f)) })
                         .OrderBy(x => x.Index >= 0 ? x.Index : int.MaxValue)
                         .ThenBy(x => x.Path)
                         .Select(x => x.Path)
                    )
                    .ToList();

                Log($"Sorted {_pageFiles.Count} page files");

                foreach (var file in _pageFiles.Take(5))
                {
                    Log($"  Page: {Path.GetFileName(file)}");
                }
                if (_pageFiles.Count > 5) Log($"  ... and {_pageFiles.Count - 5} more");

                OnPagesLoaded?.Invoke(this, _pageFiles);

                if (_pageFiles.Count > 0)
                {
                    _currentPageIndex = 0;
                    Log($"Loading first page: {Path.GetFileName(_pageFiles[0])}");
                    await LoadPageAsync(0);
                }

                OnStatusChanged?.Invoke(this, $"Loaded: {_bookTitle} ({_pageFiles.Count} pages)");
                Log($"=== BOOK LOADED SUCCESSFULLY ===");
                Log($"Log file: {GetLogFilePath()}");
                return true;
            }
            catch (Exception ex)
            {
                Log($"ERROR loading book: {ex.Message}");
                Log($"Stack trace: {ex.StackTrace}");
                OnStatusChanged?.Invoke(this, $"Error: {ex.Message}");
                return false;
            }
        }

        // ============================================================
        // Get Red Answer Content ONLY - For separate views
        // ============================================================
        public async Task<string> GetRedAnswerContentAsync(string filePath, string answerType)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileNameWithoutExtension(filePath) ?? "";
                Log($"=== GET RED ANSWER CONTENT for: {fileName} type: {answerType} ===");

                string redAnswerPath = Path.Combine(directory, fileName + "_redAnswer.html");
                
                if (!File.Exists(redAnswerPath))
                {
                    string[] altPatterns;
                    if (answerType == "teacherNotes")
                    {
                        altPatterns = new[]
                        {
                            Path.Combine(directory, fileName + "_teacherNotes.html"),
                            Path.Combine(directory, fileName + "_teacher.html"),
                            Path.Combine(directory, "teacherNotes.html"),
                            Path.Combine(directory, "teacher.html")
                        };
                    }
                    else
                    {
                        altPatterns = new[]
                        {
                            Path.Combine(directory, fileName + "_studentAnswers.html"),
                            Path.Combine(directory, fileName + "_studentAnswer.html"),
                            Path.Combine(directory, fileName + "_student.html"),
                            Path.Combine(directory, "studentAnswers.html"),
                            Path.Combine(directory, "studentAnswer.html"),
                            Path.Combine(directory, "redAnswer.html")
                        };
                    }

                    foreach (var alt in altPatterns)
                    {
                        if (File.Exists(alt))
                        {
                            redAnswerPath = alt;
                            Log($"Found alternative redAnswer at: {redAnswerPath}");
                            break;
                        }
                    }
                }

                if (!File.Exists(redAnswerPath))
                {
                    Log($"No redAnswer file found for: {fileName}");
                    return "";
                }

                string content = await File.ReadAllTextAsync(redAnswerPath);
                Log($"RedAnswer content size: {content.Length} bytes");

                content = Regex.Replace(content, @"display\s*:\s*none\s*;?", "", RegexOptions.IgnoreCase);
                content = Regex.Replace(content, @"display\s*:\s*none\s*(?=[;\s}])", "", RegexOptions.IgnoreCase);
                content = Regex.Replace(content, @"visibility\s*:\s*hidden\s*;?", "", RegexOptions.IgnoreCase);
                content = Regex.Replace(content, @"style\s*=\s*[""']\s*[""']", "", RegexOptions.IgnoreCase);
                content = Regex.Replace(content, @"display\s*:\s*none", "", RegexOptions.IgnoreCase);

                var bodyMatch = Regex.Match(content, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
                if (bodyMatch.Success)
                {
                    content = bodyMatch.Groups[1].Value;
                    Log($"Extracted content from body tag, size: {content.Length}");
                }

                string extractedContent = "";
                
                if (answerType == "teacherNotes")
                {
                    var matches = Regex.Matches(content, 
                        @"<[^>]*class\s*=\s*[""'][^""']*tbnote[^""']*[""'][^>]*>[\s\S]*?</[^>]*>", 
                        RegexOptions.IgnoreCase);
                    
                    if (matches.Count > 0)
                    {
                        Log($"Found {matches.Count} teacher note elements");
                        foreach (Match match in matches)
                        {
                            extractedContent += match.Value + "\n";
                        }
                    }
                    else
                    {
                        var matches2 = Regex.Matches(content, 
                            @"<[^>]*>[^<]*teacher[^<]*</[^>]*>", 
                            RegexOptions.IgnoreCase);
                        if (matches2.Count > 0)
                        {
                            Log($"Found {matches2.Count} teacher content elements (fallback)");
                            foreach (Match match in matches2)
                            {
                                extractedContent += match.Value + "\n";
                            }
                        }
                    }
                }
                else
                {
                    var matches = Regex.Matches(content, 
                        @"<[^>]*class\s*=\s*[""'][^""']*sa[^""']*[""'][^>]*>[\s\S]*?</[^>]*>", 
                        RegexOptions.IgnoreCase);
                    
                    if (matches.Count > 0)
                    {
                        Log($"Found {matches.Count} student answer elements");
                        foreach (Match match in matches)
                        {
                            extractedContent += match.Value + "\n";
                        }
                    }
                    else
                    {
                        var matches2 = Regex.Matches(content, 
                            @"<[^>]*>[^<]*(?:student|answer)[^<]*</[^>]*>", 
                            RegexOptions.IgnoreCase);
                        if (matches2.Count > 0)
                        {
                            Log($"Found {matches2.Count} student answer elements (fallback)");
                            foreach (Match match in matches2)
                            {
                                extractedContent += match.Value + "\n";
                            }
                        }
                        else
                        {
                            extractedContent = content;
                            Log("Using all content as student answers (fallback)");
                        }
                    }
                }

                if (string.IsNullOrEmpty(extractedContent))
                {
                    Log($"No {answerType} content found");
                    return "";
                }

                Log($"Extracted {answerType} content: {extractedContent.Length} bytes");
                return extractedContent;
            }
            catch (Exception ex)
            {
                Log($"Error getting red answer content: {ex.Message}");
                return "";
            }
        }

        // ============================================================
        // Get Step Background Image - Public for MainPage
        // ============================================================
        public string GetStepBackgroundImage(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath) ?? "";
            var stepNumber = ParseStepIndex(Path.GetFileName(filePath));
            
            Log($"Looking for background for step {stepNumber} in {directory}");
            
            var imagesPath = Path.Combine(directory, "images");
            if (Directory.Exists(imagesPath))
            {
                var imagePath = Path.Combine(imagesPath, $"steps_{stepNumber}.jpg");
                if (File.Exists(imagePath))
                {
                    Log($"Found: {imagePath}");
                    return ConvertImageToBase64HighQuality(imagePath);
                }
                
                imagePath = Path.Combine(imagesPath, $"steps_{stepNumber}.png");
                if (File.Exists(imagePath))
                {
                    Log($"Found: {imagePath}");
                    return ConvertImageToBase64HighQuality(imagePath);
                }
                
                imagePath = Path.Combine(imagesPath, $"step_{stepNumber}.jpg");
                if (File.Exists(imagePath))
                {
                    Log($"Found: {imagePath}");
                    return ConvertImageToBase64HighQuality(imagePath);
                }
                
                imagePath = Path.Combine(imagesPath, $"step_{stepNumber}.png");
                if (File.Exists(imagePath))
                {
                    Log($"Found: {imagePath}");
                    return ConvertImageToBase64HighQuality(imagePath);
                }
                
                var anyJpg = Directory.GetFiles(imagesPath, "*.jpg").FirstOrDefault();
                if (anyJpg != null)
                {
                    Log($"Using fallback JPG: {anyJpg}");
                    return ConvertImageToBase64HighQuality(anyJpg);
                }
                
                var anyPng = Directory.GetFiles(imagesPath, "*.png").FirstOrDefault();
                if (anyPng != null)
                {
                    Log($"Using fallback PNG: {anyPng}");
                    return ConvertImageToBase64HighQuality(anyPng);
                }
            }
            
            var sameDirJpg = Directory.GetFiles(directory, $"*{stepNumber}*.jpg").FirstOrDefault();
            if (sameDirJpg != null)
            {
                Log($"Found in same dir: {sameDirJpg}");
                return ConvertImageToBase64HighQuality(sameDirJpg);
            }
            
            var sameDirPng = Directory.GetFiles(directory, $"*{stepNumber}*.png").FirstOrDefault();
            if (sameDirPng != null)
            {
                Log($"Found in same dir: {sameDirPng}");
                return ConvertImageToBase64HighQuality(sameDirPng);
            }
            
            Log($"No image found for step {stepNumber}");
            return GetPlaceholderImage();
        }

        // ============================================================
        // Get Font CSS with embedded fonts - Public for MainPage
        // ============================================================
        public async Task<string> GetFontCssWithEmbeddedFonts(string directory)
        {
            try
            {
                if (_fontCache.TryGetValue(directory, out var cached))
                    return cached;

                string fontCss = "";
                string fontCssPath = "";

                var fontCssFiles = Directory.GetFiles(directory, "*_font.css");
                if (fontCssFiles.Length > 0)
                {
                    fontCssPath = fontCssFiles[0];
                }
                else
                {
                    var rootFontCss = Path.Combine(_currentBookPath, "font.css");
                    if (File.Exists(rootFontCss))
                        fontCssPath = rootFontCss;
                }

                if (!string.IsNullOrEmpty(fontCssPath) && File.Exists(fontCssPath))
                {
                    fontCss = await File.ReadAllTextAsync(fontCssPath);
                    System.Diagnostics.Debug.WriteLine($"Found font CSS: {fontCssPath}");
                    
                    string fontsFolder = Path.Combine(_currentBookPath, "FONTS");
                    if (!Directory.Exists(fontsFolder))
                        fontsFolder = Path.Combine(_currentBookPath, "fonts");

                    var fontFaceMatches = Regex.Matches(fontCss, @"@font-face\s*\{([^}]*)\}");
                    foreach (Match match in fontFaceMatches)
                    {
                        var fontFaceContent = match.Groups[1].Value;
                        
                        var familyMatch = Regex.Match(fontFaceContent, @"font-family\s*:\s*['""]?([^;'""]+)['""]?");
                        string fontFamily = familyMatch.Success ? familyMatch.Groups[1].Value.Trim() : "";
                        
                        var urlMatches = Regex.Matches(fontFaceContent, @"url\(['""]?([^)'""]+)['""]?\)");
                        foreach (Match urlMatch in urlMatches)
                        {
                            var fontPath = urlMatch.Groups[1].Value;
                            
                            fontPath = fontPath.Replace("../FONTS/", "");
                            fontPath = fontPath.Replace("../fonts/", "");
                            fontPath = fontPath.Replace("./", "");
                            
                            System.Diagnostics.Debug.WriteLine($"Looking for font: {fontPath}");
                            
                            string fullFontPath = null;
                            
                            if (Directory.Exists(fontsFolder))
                            {
                                var exactPath = Path.Combine(fontsFolder, fontPath);
                                if (File.Exists(exactPath))
                                {
                                    fullFontPath = exactPath;
                                }
                                else
                                {
                                    var fileNameOnly = Path.GetFileName(fontPath);
                                    var fileNamePath = Path.Combine(fontsFolder, fileNameOnly);
                                    if (File.Exists(fileNamePath))
                                    {
                                        fullFontPath = fileNamePath;
                                    }
                                    else
                                    {
                                        var allFonts = Directory.GetFiles(fontsFolder, "*.*");
                                        foreach (var f in allFonts)
                                        {
                                            var name = Path.GetFileName(f);
                                            var nameWithoutExt = Path.GetFileNameWithoutExtension(name);
                                            var searchName = Path.GetFileNameWithoutExtension(fontPath);
                                            
                                            if (nameWithoutExt.Equals(searchName, StringComparison.OrdinalIgnoreCase))
                                            {
                                                fullFontPath = f;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(fullFontPath) && File.Exists(fullFontPath))
                            {
                                var fontBytes = File.ReadAllBytes(fullFontPath);
                                var fontBase64 = Convert.ToBase64String(fontBytes);
                                var ext = Path.GetExtension(fullFontPath).ToLower();
                                var format = ext switch
                                {
                                    ".ttf" => "truetype",
                                    ".otf" => "opentype",
                                    ".woff" => "woff",
                                    ".woff2" => "woff2",
                                    ".eot" => "embedded-opentype",
                                    ".svg" => "svg",
                                    _ => "truetype"
                                };
                                var mimeType = ext switch
                                {
                                    ".ttf" => "font/ttf",
                                    ".otf" => "font/otf",
                                    ".woff" => "font/woff",
                                    ".woff2" => "font/woff2",
                                    ".eot" => "application/vnd.ms-fontobject",
                                    ".svg" => "image/svg+xml",
                                    _ => "font/ttf"
                                };
                                
                                var dataUri = $"data:{mimeType};base64,{fontBase64}";
                                
                                fontCss = fontCss.Replace($"url('{urlMatch.Groups[1].Value}')", $"url('{dataUri}')");
                                fontCss = fontCss.Replace($"url(\"{urlMatch.Groups[1].Value}\")", $"url('{dataUri}')");
                                fontCss = fontCss.Replace($"url({urlMatch.Groups[1].Value})", $"url('{dataUri}')");
                                
                                System.Diagnostics.Debug.WriteLine($"Embedded font: {Path.GetFileName(fullFontPath)} ({fontBytes.Length} bytes)");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"Font file not found: {fontPath} in {fontsFolder}");
                            }
                        }
                    }

                    _fontCache[directory] = fontCss;
                    return fontCss;
                }

                System.Diagnostics.Debug.WriteLine("No font CSS found, scanning for font files...");
                fontCss = await GenerateFontCssFromFiles();
                
                _fontCache[directory] = fontCss;
                return fontCss;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading fonts: {ex.Message}");
                return "";
            }
        }

        private async Task<string> GenerateFontCssFromFiles()
        {
            try
            {
                string fontCss = "";
                
                var fontsFolder = Path.Combine(_currentBookPath, "FONTS");
                if (!Directory.Exists(fontsFolder))
                    fontsFolder = Path.Combine(_currentBookPath, "fonts");
                
                if (!Directory.Exists(fontsFolder))
                    return "";

                var fontExtensions = new[] { ".ttf", ".otf", ".woff", ".woff2" };
                var fontFiles = new List<string>();

                foreach (var ext in fontExtensions)
                {
                    var files = Directory.GetFiles(fontsFolder, "*" + ext);
                    fontFiles.AddRange(files);
                }

                System.Diagnostics.Debug.WriteLine($"Found {fontFiles.Count} font files in {fontsFolder}");

                foreach (var fontFile in fontFiles)
                {
                    try
                    {
                        var fontName = Path.GetFileNameWithoutExtension(fontFile);
                        var fontBytes = File.ReadAllBytes(fontFile);
                        var fontBase64 = Convert.ToBase64String(fontBytes);
                        var ext = Path.GetExtension(fontFile).ToLower();
                        var format = ext switch
                        {
                            ".ttf" => "truetype",
                            ".otf" => "opentype",
                            ".woff" => "woff",
                            ".woff2" => "woff2",
                            _ => "truetype"
                        };
                        var mimeType = ext switch
                        {
                            ".ttf" => "font/ttf",
                            ".otf" => "font/otf",
                            ".woff" => "font/woff",
                            ".woff2" => "font/woff2",
                            _ => "font/ttf"
                        };
                        var dataUri = $"data:{mimeType};base64,{fontBase64}";

                        fontCss += $@"
                            @font-face {{
                                font-family: '{fontName}';
                                src: url('{dataUri}') format('{format}');
                                font-weight: normal;
                                font-style: normal;
                            }}";

                        System.Diagnostics.Debug.WriteLine($"Generated font: {fontName} ({fontBytes.Length} bytes)");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error processing font {fontFile}: {ex.Message}");
                    }
                }

                return fontCss;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error generating font CSS: {ex.Message}");
                return "";
            }
        }

        private string GetPlaceholderImage()
        {
            return "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='1024' height='1344'%3E%3Crect width='1024' height='1344' fill='%232d2d44'/%3E%3C/svg%3E";
        }

        private string ConvertImageToBase64HighQuality(string imagePath)
        {
            try
            {
                if (_imageCache.TryGetValue(imagePath, out var cached))
                    return cached;

                var bytes = File.ReadAllBytes(imagePath);
                var extension = Path.GetExtension(imagePath).ToLower();
                var mimeType = extension switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    ".svg" => "image/svg+xml",
                    _ => "image/jpeg"
                };
                
                var base64 = Convert.ToBase64String(bytes);
                var result = $"data:{mimeType};base64,{base64}";
                _imageCache[imagePath] = result;
                
                System.Diagnostics.Debug.WriteLine($"Loaded image: {Path.GetFileName(imagePath)} ({bytes.Length} bytes)");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading image {imagePath}: {ex.Message}");
                return GetPlaceholderImage();
            }
        }

        // ============================================================
        // Decrypt all files in the book directory - Only if needed
        // ============================================================
        private async Task DecryptBookFilesAsync(string folderPath)
        {
            try
            {
                Log($"=== DECRYPTING BOOK FILES in: {folderPath} ===");
                Log($"Book UID: {_currentBookUid}");
                
                var htmlFiles = Directory.GetFiles(folderPath, "*.html", SearchOption.AllDirectories).ToList();
                var xmlFiles = Directory.GetFiles(folderPath, "*.xml", SearchOption.AllDirectories).ToList();
                var htmFiles = Directory.GetFiles(folderPath, "*.htm", SearchOption.AllDirectories).ToList();

                Log($"Found {htmlFiles.Count} HTML files, {xmlFiles.Count} XML files, {htmFiles.Count} HTM files");

                int bookKey = _decryptionService.CalculateKey(_currentBookUid);
                Log($"Book Key: {bookKey}");

                int decryptedCount = 0;
                int alreadyDecryptedCount = 0;

                var allXmlAndHtmFiles = new List<string>();
                allXmlAndHtmFiles.AddRange(xmlFiles);
                allXmlAndHtmFiles.AddRange(htmFiles);

                // Check if HTML files are already decrypted
                bool needsDecryption = false;
                if (htmlFiles.Count > 0)
                {
                    var sampleFile = htmlFiles[0];
                    var sampleContent = await File.ReadAllTextAsync(sampleFile);
                    if (sampleContent.Contains("<") && sampleContent.Contains(">") && 
                        (sampleContent.Contains("</") || sampleContent.Contains("/>")))
                    {
                        if (sampleContent.Contains("class=") || sampleContent.Contains("<div"))
                        {
                            needsDecryption = false;
                            Log($"✅ Sample file {Path.GetFileName(sampleFile)} appears to be already decrypted");
                        }
                        else
                        {
                            needsDecryption = true;
                        }
                    }
                    else
                    {
                        needsDecryption = true;
                    }
                }
                else
                {
                    needsDecryption = true;
                }

                // Decrypt XML and HTM files
                Log("📄 Processing XML and HTM files...");
                foreach (var file in allXmlAndHtmFiles)
                {
                    try
                    {
                        var fileName = Path.GetFileName(file);
                        var content = await File.ReadAllTextAsync(file);

                        if (!_decryptionService.IsEncrypted(content))
                        {
                            Log($"  ⏭️  {fileName}: Already decrypted, skipping");
                            alreadyDecryptedCount++;
                            continue;
                        }

                        Log($"  🔓 Decrypting: {fileName}");
                        string decryptedContent = _decryptionService.DecryptXmlOrHtm(content, fileName);
                        await File.WriteAllTextAsync(file, decryptedContent);
                        decryptedCount++;
                        Log($"    ✅ Decrypted: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        Log($"  ❌ Error decrypting {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                Log($"XML/HTM files: {decryptedCount} decrypted, {alreadyDecryptedCount} already decrypted");
                Log("");

                // Decrypt HTML files if needed
                if (needsDecryption && htmlFiles.Count > 0)
                {
                    Log("📄 Processing HTML files...");
                    Log($"Using Book Key: {bookKey}");

                    int htmlDecryptedCount = 0;
                    int htmlSkippedCount = 0;

                    foreach (var file in htmlFiles)
                    {
                        try
                        {
                            var fileName = Path.GetFileName(file);
                            var content = await File.ReadAllTextAsync(file);

                            bool isDecrypted = content.Contains("<") && content.Contains(">") && 
                                               (content.Contains("</") || content.Contains("/>")) &&
                                               (content.Contains("class=") || content.Contains("<div") || content.Contains("id="));

                            if (isDecrypted)
                            {
                                Log($"  ⏭️  {fileName}: Already decrypted, skipping");
                                htmlSkippedCount++;
                                continue;
                            }

                            Log($"  🔓 Decrypting HTML: {fileName} (using Book Key: {bookKey})");
                            string decryptedContent = _decryptionService.DecryptWithKey(content, bookKey);
                            await File.WriteAllTextAsync(file, decryptedContent);
                            htmlDecryptedCount++;
                            
                            if (decryptedContent.Contains("<") && decryptedContent.Contains(">"))
                            {
                                Log($"    ✅ Decrypted: {fileName} (valid HTML)");
                            }
                            else
                            {
                                Log($"    ⚠️ Decrypted: {fileName} (may still be encrypted)");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"  ❌ Error decrypting {Path.GetFileName(file)}: {ex.Message}");
                        }
                    }

                    Log($"HTML files: {htmlDecryptedCount} decrypted, {htmlSkippedCount} already decrypted");
                    Log("");
                }
                else
                {
                    Log("⏭️ Skipping HTML decryption - files appear to be already decrypted");
                }

                Log("=== Summary ===");
                Log($"✅ XML/HTM decrypted: {decryptedCount} files");
                Log($"⏭️  XML/HTM skipped (already decrypted): {alreadyDecryptedCount} files");
            }
            catch (Exception ex)
            {
                Log($"ERROR in DecryptBookFilesAsync: {ex.Message}");
            }
        }

        public async Task DecryptBookAsync()
        {
            if (string.IsNullOrEmpty(_currentBookPath))
            {
                OnStatusChanged?.Invoke(this, "No book loaded");
                return;
            }

            OnStatusChanged?.Invoke(this, "Decrypting book files...");
            await DecryptBookFilesAsync(_currentBookPath);
            OnStatusChanged?.Invoke(this, "Decryption complete! Reloading book...");
            await LoadBookAsync(_currentBookPath);
        }

        private bool IsPureStepsFile(string fileName)
        {
            return Regex.IsMatch(fileName ?? "", @"^steps?_\d+\.html$", RegexOptions.IgnoreCase);
        }

        private int ParseStepIndex(string fileName)
        {
            try
            {
                var m = Regex.Match(fileName ?? "", @"^steps?_(\d+)\.html$", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int idx))
                    return idx;
                
                m = Regex.Match(fileName ?? "", @"^step_(\d+)\.html$", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int idx2))
                    return idx2;
                    
                m = Regex.Match(fileName ?? "", @"(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int idx3))
                    return idx3;
            }
            catch { }
            return -1;
        }

        public async Task LoadPageAsync(int index)
        {
            if (index < 0 || index >= _pageFiles.Count) return;

            try
            {
                Log($"=== LOADING PAGE {index} ===");
                _currentPageIndex = index;
                var filePath = _pageFiles[index];
                var fileName = Path.GetFileName(filePath);
                Log($"File: {fileName}");

                var content = await File.ReadAllTextAsync(filePath);
                Log($"Raw content size: {content.Length} bytes");

                var processedHtml = await ProcessHtmlContent(content, filePath);
                _currentPageHtml = processedHtml;
                Log($"Processed HTML size: {processedHtml.Length} bytes");

                OnPageChanged?.Invoke(this, processedHtml);
                OnStatusChanged?.Invoke(this, $"Viewing: {fileName}");
                Log($"=== PAGE {index} LOADED SUCCESSFULLY ===");
            }
            catch (Exception ex)
            {
                Log($"ERROR loading page: {ex.Message}");
                Log($"Stack trace: {ex.StackTrace}");
                OnStatusChanged?.Invoke(this, $"Error loading page: {ex.Message}");
            }
        }

        // ============================================================
        // Process HTML Content
        // ============================================================
        private async Task<string> ProcessHtmlContent(string htmlContent, string filePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath) ?? "";
                string fileName = Path.GetFileName(filePath) ?? "";
                Log($"=== PROCESSING HTML: {fileName} ===");

                if (IsPureStepsFile(fileName) && fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"Processing pure steps file: {fileName}");

                    string bgImage = GetStepBackgroundImage(filePath);
                    Log($"Background image: {(string.IsNullOrEmpty(bgImage) ? "NOT FOUND" : "FOUND")}");

                    string contentHtml = await ExtractContentFromHtmlFile(filePath, fileName, directory);
                    
                    string fontCss = await GetFontCssWithEmbeddedFonts(directory);
                    Log($"Font CSS: {(string.IsNullOrEmpty(fontCss) ? "NOT FOUND" : "FOUND")}");

                    string teacherTempPath = await ProcessRedAnswersToTemp(filePath, "teacherNotes");
                    string studentTempPath = await ProcessRedAnswersToTemp(filePath, "studentAnswers");

                    string teacherAnswerHtml = "";
                    string studentAnswerHtml = "";
                    
                    if (!string.IsNullOrEmpty(teacherTempPath) && File.Exists(teacherTempPath))
                    {
                        teacherAnswerHtml = await File.ReadAllTextAsync(teacherTempPath);
                        Log($"Loaded teacher notes from temp: {teacherAnswerHtml.Length} bytes");
                    }
                    
                    if (!string.IsNullOrEmpty(studentTempPath) && File.Exists(studentTempPath))
                    {
                        studentAnswerHtml = await File.ReadAllTextAsync(studentTempPath);
                        Log($"Loaded student answers from temp: {studentAnswerHtml.Length} bytes");
                    }

                    Log($"Teacher answers: {(string.IsNullOrEmpty(teacherAnswerHtml) ? "NOT FOUND" : "FOUND")}");
                    Log($"Student answers: {(string.IsNullOrEmpty(studentAnswerHtml) ? "NOT FOUND" : "FOUND")}");

                    string overlayHtml = BuildOverlayHtml(bgImage, contentHtml, fileName, fontCss, teacherAnswerHtml, studentAnswerHtml);
                    Log($"Built overlay HTML, size: {overlayHtml.Length}");

                    return overlayHtml;
                }

                Log($"Not a pure steps file, returning original content");
                return htmlContent;
            }
            catch (Exception ex)
            {
                Log($"ERROR in ProcessHtmlContent: {ex.Message}");
                Log($"Stack trace: {ex.StackTrace}");
                return htmlContent;
            }
        }

        // ============================================================
        // Extract Content from HTML file
        // ============================================================
        private async Task<string> ExtractContentFromHtmlFile(string filePath, string fileName, string directory)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string contentHtml = "";

            string oriHtmlPath = Path.Combine(directory, nameWithoutExt + "_ori.html");
            if (File.Exists(oriHtmlPath))
            {
                Log($"Found _ori.html: {oriHtmlPath}");
                string oriContent = await File.ReadAllTextAsync(oriHtmlPath);
                contentHtml = ExtractContentFromHtml(oriContent);
                Log($"Loaded content from _ori.html, size: {contentHtml.Length}");
                return contentHtml;
            }

            string paraXmlPath = Path.Combine(directory, nameWithoutExt + "_para.xml");
            if (File.Exists(paraXmlPath))
            {
                Log($"Found _para.xml: {paraXmlPath}");
                string paraContent = await File.ReadAllTextAsync(paraXmlPath);
                contentHtml = ExtractContentFromParaXml(paraContent);
                Log($"Loaded content from _para.xml, size: {contentHtml.Length}");
                return contentHtml;
            }

            try
            {
                string fallbackContent = await File.ReadAllTextAsync(filePath);
                contentHtml = ExtractContentFromHtml(fallbackContent);
                Log($"Loaded content from original HTML, size: {contentHtml.Length}");
            }
            catch (Exception ex)
            {
                Log($"Error reading fallback content: {ex.Message}");
                contentHtml = "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";
            }
            
            return contentHtml;
        }

        private string ExtractContentFromHtml(string htmlContent)
        {
            var bodyMatch = Regex.Match(htmlContent, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
            if (!bodyMatch.Success)
                return htmlContent;

            return bodyMatch.Groups[1].Value;
        }

        private string ExtractContentFromParaXml(string paraXmlContent)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(paraXmlContent);

                var parasNode = doc.SelectSingleNode("//paras");
                if (parasNode != null)
                {
                    var result = "";
                    foreach (XmlNode child in parasNode.ChildNodes)
                    {
                        if (child.Name == "para")
                        {
                            var text = child.InnerText;
                            var style = child.Attributes?["style"]?.Value ?? "";
                            result += $"<div class='para' style='{style}'>{text}</div>";
                        }
                    }
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log($"Error parsing para.xml: {ex.Message}");
            }
            return "";
        }

        // ============================================================
        // Process Red Answers - For the main WebView (overlay mode)
        // ============================================================
        private async Task<string> ProcessRedAnswersToTemp(string filePath, string answerType)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileNameWithoutExtension(filePath) ?? "";
                Log($"=== PROCESS RED ANSWERS for: {fileName} type: {answerType} ===");

                string redAnswerPath = Path.Combine(directory, fileName + "_redAnswer.html");
                Log($"Looking for redAnswer at: {redAnswerPath}");
                
                if (!File.Exists(redAnswerPath))
                {
                    string[] altPatterns;
                    if (answerType == "teacherNotes")
                    {
                        altPatterns = new[]
                        {
                            Path.Combine(directory, fileName + "_teacherNotes.html"),
                            Path.Combine(directory, fileName + "_teacher.html"),
                            Path.Combine(directory, "teacherNotes.html"),
                            Path.Combine(directory, "teacher.html")
                        };
                    }
                    else
                    {
                        altPatterns = new[]
                        {
                            Path.Combine(directory, fileName + "_studentAnswers.html"),
                            Path.Combine(directory, fileName + "_studentAnswer.html"),
                            Path.Combine(directory, fileName + "_student.html"),
                            Path.Combine(directory, "studentAnswers.html"),
                            Path.Combine(directory, "studentAnswer.html"),
                            Path.Combine(directory, "redAnswer.html")
                        };
                    }

                    foreach (var alt in altPatterns)
                    {
                        if (File.Exists(alt))
                        {
                            redAnswerPath = alt;
                            Log($"Found alternative redAnswer at: {redAnswerPath}");
                            break;
                        }
                    }
                }

                if (!File.Exists(redAnswerPath))
                {
                    Log($"No redAnswer file found for: {fileName} type: {answerType}");
                    return "";
                }

                string redAnswerContent = await File.ReadAllTextAsync(redAnswerPath);
                Log($"RedAnswer content size: {redAnswerContent.Length} bytes");

                redAnswerContent = Regex.Replace(redAnswerContent, @"display\s*:\s*none\s*;?", "", RegexOptions.IgnoreCase);
                redAnswerContent = Regex.Replace(redAnswerContent, @"display\s*:\s*none\s*(?=[;\s}])", "", RegexOptions.IgnoreCase);
                redAnswerContent = Regex.Replace(redAnswerContent, @"visibility\s*:\s*hidden\s*;?", "", RegexOptions.IgnoreCase);
                redAnswerContent = Regex.Replace(redAnswerContent, @"style\s*=\s*[""']\s*[""']", "", RegexOptions.IgnoreCase);
                redAnswerContent = Regex.Replace(redAnswerContent, @"display\s*:\s*none", "", RegexOptions.IgnoreCase);
                
                redAnswerContent = Regex.Replace(redAnswerContent, 
                    @"style\s*=\s*[""']([^""']*)display\s*:\s*none\s*;?\s*([^""']*)[""']", 
                    m => {
                        string before = m.Groups[1].Value;
                        string after = m.Groups[2].Value;
                        string combined = (before + " " + after).Trim();
                        if (string.IsNullOrEmpty(combined))
                            return "";
                        return $"style=\"{combined}\"";
                    }, RegexOptions.IgnoreCase);
                
                Log($"After removing display:none, content size: {redAnswerContent.Length}");

                var bodyMatch = Regex.Match(redAnswerContent, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
                if (bodyMatch.Success)
                {
                    redAnswerContent = bodyMatch.Groups[1].Value;
                    Log($"Extracted content from body tag, size: {redAnswerContent.Length}");
                }

                string extractedContent = "";
                
                if (answerType == "teacherNotes")
                {
                    var matches = Regex.Matches(redAnswerContent, 
                        @"<[^>]*class\s*=\s*[""'][^""']*tbnote[^""']*[""'][^>]*>[\s\S]*?</[^>]*>", 
                        RegexOptions.IgnoreCase);
                    
                    if (matches.Count > 0)
                    {
                        Log($"Found {matches.Count} teacher note elements");
                        foreach (Match match in matches)
                        {
                            extractedContent += match.Value;
                        }
                    }
                    else
                    {
                        var matches2 = Regex.Matches(redAnswerContent, 
                            @"<[^>]*>[^<]*teacher[^<]*</[^>]*>", 
                            RegexOptions.IgnoreCase);
                        if (matches2.Count > 0)
                        {
                            Log($"Found {matches2.Count} teacher content elements (fallback)");
                            foreach (Match match in matches2)
                            {
                                extractedContent += match.Value;
                            }
                        }
                        else
                        {
                            var svgMatches = Regex.Matches(redAnswerContent, 
                                @"<svg[^>]*class\s*=\s*[""'][^""']*tbnote[^""']*[""'][^>]*>[\s\S]*?</svg>", 
                                RegexOptions.IgnoreCase);
                            if (svgMatches.Count > 0)
                            {
                                Log($"Found {svgMatches.Count} SVG teacher note elements");
                                foreach (Match match in svgMatches)
                                {
                                    extractedContent += match.Value;
                                }
                            }
                        }
                    }
                }
                else
                {
                    var matches = Regex.Matches(redAnswerContent, 
                        @"<[^>]*class\s*=\s*[""'][^""']*sa[^""']*[""'][^>]*>[\s\S]*?</[^>]*>", 
                        RegexOptions.IgnoreCase);
                    
                    if (matches.Count > 0)
                    {
                        Log($"Found {matches.Count} student answer elements");
                        foreach (Match match in matches)
                        {
                            extractedContent += match.Value;
                        }
                    }
                    else
                    {
                        var matches2 = Regex.Matches(redAnswerContent, 
                            @"<[^>]*>[^<]*(?:student|answer)[^<]*</[^>]*>", 
                            RegexOptions.IgnoreCase);
                        if (matches2.Count > 0)
                        {
                            Log($"Found {matches2.Count} student answer elements (fallback)");
                            foreach (Match match in matches2)
                            {
                                extractedContent += match.Value;
                            }
                        }
                        else
                        {
                            extractedContent = redAnswerContent;
                            Log("Using all content as student answers (fallback)");
                        }
                    }
                }

                if (string.IsNullOrEmpty(extractedContent))
                {
                    Log($"No {answerType} content found");
                    return "";
                }

                bool shouldShow = (answerType == "teacherNotes" && _showTeacherNotes) ||
                                  (answerType == "studentAnswers" && _showStudentAnswers);
                
                string displayStyle = shouldShow ? "inline-block" : "none";
                string typeAttribute = answerType == "teacherNotes" ? "teacherNotes" : "studentAnswers";
                
                Log($"Display {answerType}: {displayStyle} (show: {shouldShow})");

                string wrappedContent = $@"
                    <div redanswertype='{typeAttribute}' style='position:relative; z-index:10; display:{displayStyle}; pointer-events:none;'>
                        {extractedContent}
                    </div>";

                string tempFilePath = Path.Combine(_tempFolder, $"{fileName}_{answerType}.html");
                await File.WriteAllTextAsync(tempFilePath, wrappedContent);
                Log($"Saved processed {answerType} to temp: {tempFilePath} ({wrappedContent.Length} bytes)");

                return tempFilePath;
            }
            catch (Exception ex)
            {
                Log($"Error processing {answerType}: {ex.Message}");
                return "";
            }
        }

        // ============================================================
        // Build Overlay HTML - For the main WebView
        // ============================================================
        private string BuildOverlayHtml(string bgImage, string contentHtml, string fileName, string fontCss, string teacherAnswerHtml, string studentAnswerHtml)
        {
            if (string.IsNullOrEmpty(bgImage))
            {
                bgImage = GetPlaceholderImage();
            }

            if (string.IsNullOrEmpty(contentHtml))
            {
                contentHtml = "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";
            }

            double zoom = _currentZoom;

            string result = $@"
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset='UTF-8'>
                <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=5.0, user-scalable=yes'>
                <title>{fileName}</title>
                <style>
                    {fontCss}
                    * {{
                        margin: 0;
                        padding: 0;
                        box-sizing: border-box;
                    }}
                    html, body {{
                        width: 100%;
                        height: 100%;
                        overflow: auto;
                        background: #1a1a2e;
                        -webkit-font-smoothing: antialiased;
                        -moz-osx-font-smoothing: grayscale;
                    }}
                    body {{
                        display: flex;
                        justify-content: center;
                        align-items: center;
                        min-height: 100vh;
                        padding: 10px;
                        margin: 0;
                        overflow: auto;
                    }}
                    .page-wrapper {{
                        display: flex;
                        justify-content: center;
                        align-items: center;
                        width: 100%;
                        height: 100%;
                        min-height: 100vh;
                        overflow: auto;
                    }}
                    .page-container {{
                        position: relative;
                        width: 1024px;
                        height: 1344px;
                        flex-shrink: 0;
                        background: #2d2d44;
                        box-shadow: 0 0 30px rgba(0,0,0,0.5);
                        overflow: hidden;
                        border-radius: 4px;
                        transform: scale({zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)});
                        transform-origin: center center;
                    }}
                    .background-img {{
                        position: absolute;
                        top: 0;
                        left: 0;
                        width: 100%;
                        height: 100%;
                        object-fit: contain;
                        pointer-events: none;
                        z-index: 1;
                        image-rendering: auto;
                        image-rendering: -webkit-optimize-contrast;
                    }}
                    .content-overlay {{
                        position: absolute;
                        top: 0;
                        left: 0;
                        width: 100%;
                        height: 100%;
                        z-index: 2;
                        overflow: hidden;
                        pointer-events: auto;
                    }}
                    .content-overlay > * {{
                        position: absolute !important;
                        top: 0;
                        left: 0;
                    }}
                    .content-overlay [style*='position:absolute'],
                    .content-overlay [style*='position:fixed'] {{
                        position: absolute !important;
                    }}
                    .content-overlay [style*='position:relative'] {{
                        position: absolute !important;
                    }}
                    
                    [redanswertype='teacherNotes'] {{
                        z-index: 10;
                        background: rgba(255, 255, 0, 0.15);
                        border: 2px solid #3498db;
                        border-radius: 4px;
                        padding: 4px;
                        pointer-events: none;
                    }}
                    
                    [redanswertype='studentAnswers'] {{
                        z-index: 10;
                        background: rgba(0, 255, 0, 0.15);
                        border: 2px solid #2ecc71;
                        border-radius: 4px;
                        padding: 4px;
                        pointer-events: none;
                    }}
                    
                    .tbnote.has2 {{
                        cursor: pointer;
                    }}
                    
                    .zoom-hint {{
                        position: fixed;
                        bottom: 20px;
                        left: 50%;
                        transform: translateX(-50%);
                        color: #8899bb;
                        font-size: 12px;
                        background: rgba(0,0,0,0.7);
                        padding: 4px 12px;
                        border-radius: 12px;
                        pointer-events: none;
                        z-index: 100;
                        opacity: 0.6;
                        font-family: Arial, sans-serif;
                    }}
                    
                    ::-webkit-scrollbar {{
                        width: 6px;
                        height: 6px;
                    }}
                    ::-webkit-scrollbar-track {{
                        background: #1a1a2e;
                    }}
                    ::-webkit-scrollbar-thumb {{
                        background: #2d2d44;
                        border-radius: 3px;
                    }}
                    ::-webkit-scrollbar-thumb:hover {{
                        background: #3d3d54;
                    }}
                </style>
            </head>
            <body>
                <div class='page-wrapper'>
                    <div class='page-container'>
                        <img class='background-img' src='{bgImage}' alt='Background' />
                        <div class='content-overlay'>
                            {contentHtml}
                            {teacherAnswerHtml}
                            {studentAnswerHtml}
                        </div>
                    </div>
                </div>
                <div class='zoom-hint'>🔍 Pinch to zoom | Zoom: {zoom:F1}x</div>
            </body>
            </html>";

            return result;
        }

        public void NavigatePrevious()
        {
            if (_currentPageIndex > 0)
                _ = LoadPageAsync(_currentPageIndex - 1);
        }

        public void NavigateNext()
        {
            if (_currentPageIndex < _pageFiles.Count - 1)
                _ = LoadPageAsync(_currentPageIndex + 1);
        }

        public void ToggleAnswers(bool show)
        {
            OnToggleAnswersRequested?.Invoke(this, show);
            OnStatusChanged?.Invoke(this, show ? "Answers shown" : "Answers hidden");
        }
    }
}
