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
        private bool _twoPageSpread = false;
        private string _nextPageHtml = "";
        private double _currentZoom = 1.0;
        private double _baseScale = 1.0;
        private string _tempFolder;

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
            }
        }

        public event EventHandler<List<string>> OnPagesLoaded;
        public event EventHandler<string> OnPageChanged;
        public event EventHandler<string> OnStatusChanged;
        public event EventHandler<string> OnBookLoaded;
        public event EventHandler<bool> OnToggleAnswersRequested;
        public event EventHandler<bool> OnSideBySideToggled;
        public event EventHandler<bool> OnTwoPageSpreadToggled;
        public event EventHandler<double> OnZoomChanged;

        public List<string> PageFiles => _pageFiles;
        public int CurrentPageIndex => _currentPageIndex;
        public string CurrentBookPath => _currentBookPath;
        public string BookTitle => _bookTitle;
        public string CurrentPageHtml => _currentPageHtml;
        public bool SideBySideMode => _sideBySideMode;
        public bool TwoPageSpread => _twoPageSpread;
        public string NextPageHtml => _nextPageHtml;
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

        
        public async Task<string> BuildPageHtmlForRenderAsync(
            string filePath,
            bool includeAnswers,
            bool includeTeacherNotes,
            bool includeStudentAnswers)
        {
            var directory = Path.GetDirectoryName(filePath) ?? "";
            var fileName = Path.GetFileName(filePath) ?? "";
        
            string bgImage = GetStepBackgroundImage(filePath);
            string contentHtml = await ExtractContentFromHtmlFile(filePath, fileName, directory);
            string fontCss = await GetFontCssWithEmbeddedFonts(directory);
        
            string teacherHtml = "";
            string studentHtml = "";
        
            if (includeAnswers)
            {
                if (includeTeacherNotes)
                {
                    var redContent = await GetRedAnswerContentAsync(filePath, "teacherNotes");
                    if (!string.IsNullOrEmpty(redContent))
                    {
                        teacherHtml = $@"<div style='position:absolute;top:0;left:0;width:100%;height:100%;z-index:20;pointer-events:none;'>
                            <style>.tbnote {{ background: rgba(255,255,0,0.25); border: 3px solid #3498db; border-radius: 4px; padding: 3px; }}</style>
                            {redContent}
                        </div>";
                    }
                }
        
                if (includeStudentAnswers)
                {
                    var redContent = await GetRedAnswerContentAsync(filePath, "studentAnswers");
                    if (!string.IsNullOrEmpty(redContent))
                    {
                        studentHtml = $@"<div style='position:absolute;top:0;left:0;width:100%;height:100%;z-index:30;pointer-events:none;'>
                            <style>.sa {{ background: rgba(255,255,0,0.25); border: 3px solid #2ecc71; border-radius: 4px; padding: 3px; }}</style>
                            {redContent}
                        </div>";
                    }
                }
            }
        
            return $@"<!DOCTYPE html>
        <html>
        <head>
        <meta charset='UTF-8'>
        <style>
            {fontCss}
            * {{ margin: 0; padding: 0; box-sizing: border-box; }}
            html, body {{
                width: 1024px;
                height: 1344px;
                overflow: hidden;
                background: #ffffff;
            }}
            .page-container {{
                position: relative;
                width: 1024px;
                height: 1344px;
                background: #ffffff;
                overflow: hidden;
            }}
            .background-img {{
                position: absolute;
                top: 0; left: 0;
                width: 100%; height: 100%;
                object-fit: contain;
                pointer-events: none;
                z-index: 1;
            }}
            .content-overlay {{
                position: absolute;
                top: 0; left: 0;
                width: 100%; height: 100%;
                z-index: 2;
            }}
            .content-overlay > * {{ position: absolute !important; }}
        </style>
        </head>
        <body>
        <div class='page-container'>
            <img class='background-img' src='{bgImage}' />
            <div class='content-overlay'>
                {contentHtml}
                {teacherHtml}
                {studentHtml}
            </div>
        </div>
        </body>
        </html>";
        }


        public void ToggleTwoPageSpread()
        {
            _twoPageSpread = !_twoPageSpread;
            _sideBySideMode = _twoPageSpread;
            OnTwoPageSpreadToggled?.Invoke(this, _twoPageSpread);

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

                _bookTitle = "Unknown Book";
                var titleMatch = Regex.Match(content, @"name=""([^""]+)""");
                if (titleMatch.Success) _bookTitle = titleMatch.Groups[1].Value;
                Log($"Book title: {_bookTitle}");
                Log($"Book UID: {_currentBookUid}");

                OnBookLoaded?.Invoke(this, _bookTitle);

                if (bookXmlDecrypted)
                {
                    await DecryptBookFilesAsync(folderPath);
                }
                else
                {
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

                OnPagesLoaded?.Invoke(this, _pageFiles);

                if (_pageFiles.Count > 0)
                {
                    _currentPageIndex = 0;
                    Log($"Loading first page: {Path.GetFileName(_pageFiles[0])}");
                    await LoadPageAsync(0);
                }
                else
                {
                    OnStatusChanged?.Invoke(this, "No pages found in this book");
                    return false;
                }

                OnStatusChanged?.Invoke(this, $"Loaded: {_bookTitle} ({_pageFiles.Count} pages)");
                Log($"=== BOOK LOADED SUCCESSFULLY ===");
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
                }

                string extractedContent = "";

                if (answerType == "teacherNotes")
                {
                    var matches = Regex.Matches(content,
                        @"<[^>]*class\s*=\s*[""'][^""']*tbnote[^""']*[""'][^>]*>[\s\S]*?</[^>]*>",
                        RegexOptions.IgnoreCase);

                    if (matches.Count > 0)
                    {
                        foreach (Match match in matches)
                        {
                            extractedContent += match.Value + "\n";
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
                        foreach (Match match in matches)
                        {
                            extractedContent += match.Value + "\n";
                        }
                    }
                    else
                    {
                        extractedContent = content;
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

        public string GetStepBackgroundImage(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath) ?? "";
            var stepNumber = ParseStepIndex(Path.GetFileName(filePath));

            var imagesPath = Path.Combine(directory, "images");
            if (Directory.Exists(imagesPath))
            {
                var imagePath = Path.Combine(imagesPath, $"steps_{stepNumber}.jpg");
                if (File.Exists(imagePath)) return ConvertImageToBase64HighQuality(imagePath);

                imagePath = Path.Combine(imagesPath, $"steps_{stepNumber}.png");
                if (File.Exists(imagePath)) return ConvertImageToBase64HighQuality(imagePath);

                imagePath = Path.Combine(imagesPath, $"step_{stepNumber}.jpg");
                if (File.Exists(imagePath)) return ConvertImageToBase64HighQuality(imagePath);

                imagePath = Path.Combine(imagesPath, $"step_{stepNumber}.png");
                if (File.Exists(imagePath)) return ConvertImageToBase64HighQuality(imagePath);

                var anyJpg = Directory.GetFiles(imagesPath, "*.jpg").FirstOrDefault();
                if (anyJpg != null) return ConvertImageToBase64HighQuality(anyJpg);

                var anyPng = Directory.GetFiles(imagesPath, "*.png").FirstOrDefault();
                if (anyPng != null) return ConvertImageToBase64HighQuality(anyPng);
            }

            return GetPlaceholderImage();
        }

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

                    string fontsFolder = Path.Combine(_currentBookPath, "FONTS");
                    if (!Directory.Exists(fontsFolder))
                        fontsFolder = Path.Combine(_currentBookPath, "fonts");

                    var fontFaceMatches = Regex.Matches(fontCss, @"@font-face\s*\{([^}]*)\}");
                    foreach (Match match in fontFaceMatches)
                    {
                        var fontFaceContent = match.Groups[1].Value;
                        var urlMatches = Regex.Matches(fontFaceContent, @"url\(['""]?([^)'""]+)['""]?\)");
                        foreach (Match urlMatch in urlMatches)
                        {
                            var fontPath = urlMatch.Groups[1].Value;
                            fontPath = fontPath.Replace("../FONTS/", "").Replace("../fonts/", "").Replace("./", "");

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
                                        fullFontPath = fileNamePath;
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
                            }
                        }
                    }

                    _fontCache[directory] = fontCss;
                    return fontCss;
                }

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
                    fontFiles.AddRange(Directory.GetFiles(fontsFolder, "*" + ext));
                }

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
                    }
                    catch { }
                }

                return fontCss;
            }
            catch
            {
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

                return result;
            }
            catch
            {
                return GetPlaceholderImage();
            }
        }

        private async Task DecryptBookFilesAsync(string folderPath)
        {
            try
            {
                var htmlFiles = Directory.GetFiles(folderPath, "*.html", SearchOption.AllDirectories).ToList();
                var xmlFiles = Directory.GetFiles(folderPath, "*.xml", SearchOption.AllDirectories).ToList();
                var htmFiles = Directory.GetFiles(folderPath, "*.htm", SearchOption.AllDirectories).ToList();

                int bookKey = _decryptionService.CalculateKey(_currentBookUid);
                int decryptedCount = 0;
                int alreadyDecryptedCount = 0;

                var allXmlAndHtmFiles = new List<string>();
                allXmlAndHtmFiles.AddRange(xmlFiles);
                allXmlAndHtmFiles.AddRange(htmFiles);

                foreach (var file in allXmlAndHtmFiles)
                {
                    try
                    {
                        var fileName = Path.GetFileName(file);
                        var content = await File.ReadAllTextAsync(file);

                        if (!_decryptionService.IsEncrypted(content))
                        {
                            alreadyDecryptedCount++;
                            continue;
                        }

                        string decryptedContent = _decryptionService.DecryptXmlOrHtm(content, fileName);
                        await File.WriteAllTextAsync(file, decryptedContent);
                        decryptedCount++;
                    }
                    catch (Exception ex)
                    {
                        Log($"  ❌ Error decrypting {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

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
                            htmlSkippedCount++;
                            continue;
                        }

                        string decryptedContent = _decryptionService.DecryptWithKey(content, bookKey);
                        await File.WriteAllTextAsync(file, decryptedContent);
                        htmlDecryptedCount++;
                    }
                    catch (Exception ex)
                    {
                        Log($"  ❌ Error decrypting {Path.GetFileName(file)}: {ex.Message}");
                    }
                }
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

                if (_twoPageSpread && index + 1 < _pageFiles.Count)
                {
                    var nextFilePath = _pageFiles[index + 1];
                    var nextContent = await File.ReadAllTextAsync(nextFilePath);
                    var nextProcessedHtml = await ProcessHtmlContent(nextContent, nextFilePath);
                    _nextPageHtml = nextProcessedHtml;
                }
                else
                {
                    _nextPageHtml = "";
                }

                OnPageChanged?.Invoke(this, processedHtml);
                OnStatusChanged?.Invoke(this, $"Viewing: {fileName} ({index + 1}/{_pageFiles.Count})");
                OnPagesLoaded?.Invoke(this, _pageFiles);

                Log($"=== PAGE {index} LOADED SUCCESSFULLY ===");
            }
            catch (Exception ex)
            {
                Log($"ERROR loading page: {ex.Message}");
                OnStatusChanged?.Invoke(this, $"Error loading page: {ex.Message}");
            }
        }

        private async Task<string> ProcessHtmlContent(string htmlContent, string filePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath) ?? "";
                string fileName = Path.GetFileName(filePath) ?? "";

                if (IsPureStepsFile(fileName) && fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    string bgImage = GetStepBackgroundImage(filePath);
                    string contentHtml = await ExtractContentFromHtmlFile(filePath, fileName, directory);
                    string fontCss = await GetFontCssWithEmbeddedFonts(directory);

                    return BuildOverlayHtml(bgImage, contentHtml, fileName, fontCss, "", "");
                }

                return htmlContent;
            }
            catch (Exception ex)
            {
                Log($"ERROR in ProcessHtmlContent: {ex.Message}");
                return htmlContent;
            }
        }

        private async Task<string> ExtractContentFromHtmlFile(string filePath, string fileName, string directory)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string contentHtml = "";

            string oriHtmlPath = Path.Combine(directory, nameWithoutExt + "_ori.html");
            if (File.Exists(oriHtmlPath))
            {
                string oriContent = await File.ReadAllTextAsync(oriHtmlPath);
                contentHtml = ExtractContentFromHtml(oriContent);
                return contentHtml;
            }

            string paraXmlPath = Path.Combine(directory, nameWithoutExt + "_para.xml");
            if (File.Exists(paraXmlPath))
            {
                string paraContent = await File.ReadAllTextAsync(paraXmlPath);
                contentHtml = ExtractContentFromParaXml(paraContent);
                return contentHtml;
            }

            try
            {
                string fallbackContent = await File.ReadAllTextAsync(filePath);
                contentHtml = ExtractContentFromHtml(fallbackContent);
            }
            catch
            {
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
            catch { }
            return "";
        }

        private string BuildOverlayHtml(string bgImage, string contentHtml, string fileName, string fontCss, string teacherAnswerHtml, string studentAnswerHtml)
        {
            if (string.IsNullOrEmpty(bgImage))
                bgImage = GetPlaceholderImage();

            if (string.IsNullOrEmpty(contentHtml))
                contentHtml = "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";

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
                    * {{ margin: 0; padding: 0; box-sizing: border-box; }}
                    html, body {{
                        width: 100%;
                        height: 100%;
                        overflow: auto;
                        background: #1a1a2e;
                    }}
                    body {{
                        display: flex;
                        justify-content: center;
                        align-items: flex-start;
                        min-height: 100vh;
                        padding: 10px;
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
                        transform-origin: top center;
                    }}
                    .background-img {{
                        position: absolute;
                        top: 0; left: 0;
                        width: 100%; height: 100%;
                        object-fit: contain;
                        pointer-events: none;
                        z-index: 1;
                    }}
                    .content-overlay {{
                        position: absolute;
                        top: 0; left: 0;
                        width: 100%; height: 100%;
                        z-index: 2;
                    }}
                    .content-overlay > * {{
                        position: absolute !important;
                    }}
                    ::-webkit-scrollbar {{ width: 6px; height: 6px; }}
                    ::-webkit-scrollbar-track {{ background: #1a1a2e; }}
                    ::-webkit-scrollbar-thumb {{ background: #2d2d44; border-radius: 3px; }}
                </style>
            </head>
            <body>
                <div class='page-container'>
                    <img class='background-img' src='{bgImage}' alt='' />
                    <div class='content-overlay'>
                        {contentHtml}
                        {teacherAnswerHtml}
                        {studentAnswerHtml}
                    </div>
                </div>
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
