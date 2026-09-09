using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Color = Microsoft.Maui.Graphics.Color;

namespace BookViewer;

public partial class MainPage : ContentPage
{
    private readonly BookService _bookService = new();
    private bool _showTeacherNotes = false;
    private bool _showStudentAnswers = false;
    private string _currentContentHtml = "";
    private string _currentTeacherHtml = "";
    private string _currentStudentHtml = "";
    private string _currentViewMode = "content"; // content, teacher, student

    // File logger for MainPage
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
            _logFilePath = Path.Combine(logFolder, $"MainPage_{DateTime.Now:yyyyMMdd_HHmmss}.log");
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
                Device.BeginInvokeOnMainThread(() => StatusLabel.Text = message);
            }
            else
            {
                Device.BeginInvokeOnMainThread(() => StatusLabel.Text = message.Substring(0, 77) + "...");
            }
        }
        catch
        {
            // Ignore logging errors
        }
    }

    public MainPage()
    {
        InitializeComponent();
        Log("=== MAIN PAGE INITIALIZED ===");
        Log($"Log file: {GetLogFilePath()}");

        _bookService.OnPagesLoaded += (s, pages) =>
            Device.BeginInvokeOnMainThread(UpdateUI);

        _bookService.OnPageChanged += (s, content) =>
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                Log($"=== PAGE CHANGED ===");
                Log($"Content length: {content?.Length ?? 0}");
                
                // Store and display content
                _currentContentHtml = content;
                _currentViewMode = "content";
                UpdateViewModeButton();
                ContentWebView.Source = new HtmlWebViewSource { Html = content };
                
                // Preload teacher and student views
                LoadTeacherView();
                LoadStudentView();
            });
        };

        _bookService.OnStatusChanged += (s, msg) =>
            Device.BeginInvokeOnMainThread(() => StatusLabel.Text = msg);

        _bookService.OnBookLoaded += (s, title) =>
            Device.BeginInvokeOnMainThread(() => BookTitleLabel.Text = title);

        _bookService.OnSideBySideToggled += (s, enabled) =>
            Device.BeginInvokeOnMainThread(() =>
            {
                SideBySideButton.Text = enabled ? "📄 1/1" : "📄 1/2";
                SideBySideButton.BackgroundColor = enabled 
                    ? Color.FromArgb("#3498db") 
                    : Color.FromArgb("#2c3e50");
                StatusLabel.Text = enabled ? "Side-by-side mode" : "Single page mode";
            });

        _bookService.OnZoomChanged += (s, zoom) =>
            Device.BeginInvokeOnMainThread(() =>
            {
                ZoomLabel.Text = $"{zoom:F1}x";
            });
    }

    private async void LoadTeacherView()
    {
        try
        {
            if (_bookService.CurrentPageIndex >= 0 && _bookService.CurrentPageIndex < _bookService.PageFiles.Count)
            {
                var filePath = _bookService.PageFiles[_bookService.CurrentPageIndex];
                var directory = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileName(filePath) ?? "";
                
                // Get the base content (text from _ori.html)
                string baseContent = await GetBaseContentAsync(filePath);
                
                // Get the red answer content (teacher notes)
                string redContent = await _bookService.GetRedAnswerContentAsync(filePath, "teacherNotes");
                
                // Get background image and font CSS
                string bgImage = _bookService.GetStepBackgroundImage(filePath);
                string fontCss = await _bookService.GetFontCssWithEmbeddedFonts(directory);
                
                // Build the HTML for teacher view with both base content and highlighted answers
                _currentTeacherHtml = BuildFullViewHtml(bgImage, baseContent, redContent, fileName, fontCss, "teacherNotes");
                Log($"Teacher view loaded, length: {_currentTeacherHtml.Length}");
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading teacher view: {ex.Message}");
        }
    }

    private async void LoadStudentView()
    {
        try
        {
            if (_bookService.CurrentPageIndex >= 0 && _bookService.CurrentPageIndex < _bookService.PageFiles.Count)
            {
                var filePath = _bookService.PageFiles[_bookService.CurrentPageIndex];
                var directory = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileName(filePath) ?? "";
                
                // Get the base content (text from _ori.html)
                string baseContent = await GetBaseContentAsync(filePath);
                
                // Get the red answer content (student answers)
                string redContent = await _bookService.GetRedAnswerContentAsync(filePath, "studentAnswers");
                
                // Get background image and font CSS
                string bgImage = _bookService.GetStepBackgroundImage(filePath);
                string fontCss = await _bookService.GetFontCssWithEmbeddedFonts(directory);
                
                // Build the HTML for student view with both base content and highlighted answers
                _currentStudentHtml = BuildFullViewHtml(bgImage, baseContent, redContent, fileName, fontCss, "studentAnswers");
                Log($"Student view loaded, length: {_currentStudentHtml.Length}");
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading student view: {ex.Message}");
        }
    }

    private async Task<string> GetBaseContentAsync(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath) ?? "";
            var fileName = Path.GetFileName(filePath) ?? "";
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            
            string oriHtmlPath = Path.Combine(directory, nameWithoutExt + "_ori.html");
            if (File.Exists(oriHtmlPath))
            {
                string oriContent = await File.ReadAllTextAsync(oriHtmlPath);
                var bodyMatch = Regex.Match(oriContent, @"<body[^>]*>([\s\S]*?)</body>", RegexOptions.IgnoreCase);
                if (bodyMatch.Success)
                {
                    return bodyMatch.Groups[1].Value;
                }
                return oriContent;
            }
            
            string paraXmlPath = Path.Combine(directory, nameWithoutExt + "_para.xml");
            if (File.Exists(paraXmlPath))
            {
                string paraContent = await File.ReadAllTextAsync(paraXmlPath);
                // Parse para.xml and convert to HTML
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(paraContent);
                var parasNode = doc.SelectSingleNode("//paras");
                if (parasNode != null)
                {
                    var result = "";
                    foreach (System.Xml.XmlNode child in parasNode.ChildNodes)
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
                return "";
            }
            
            return "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";
        }
        catch (Exception ex)
        {
            Log($"Error getting base content: {ex.Message}");
            return "<div style='padding:20px;color:#666;font-size:24px;'>Content not available</div>";
        }
    }

    private string BuildFullViewHtml(string bgImage, string baseContent, string redContent, string fileName, string fontCss, string viewType)
    {
        double zoom = _bookService.CurrentZoom;
        
        if (string.IsNullOrEmpty(bgImage))
        {
            bgImage = GetPlaceholderImage();
        }

        string labelText = viewType == "teacherNotes" ? "👨‍🏫 Teacher Notes" : "👨‍🎓 Student Answers";
        string borderColor = viewType == "teacherNotes" ? "#3498db" : "#2ecc71";
        string highlightClass = viewType == "teacherNotes" ? "tbnote" : "sa";
        string labelColor = viewType == "teacherNotes" ? "52, 152, 219" : "46, 204, 113";

        return $@"
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
                }}
                
                .base-content {{
                    position: absolute;
                    top: 0;
                    left: 0;
                    width: 100%;
                    height: 100%;
                    z-index: 5;
                    pointer-events: none;
                }}
                .base-content > * {{
                    position: absolute !important;
                }}
                
                .highlight-overlay {{
                    position: absolute;
                    top: 0;
                    left: 0;
                    width: 100%;
                    height: 100%;
                    z-index: 10;
                    pointer-events: none;
                    overflow: visible;
                }}
                .highlight-overlay > * {{
                    position: absolute !important;
                }}
                
                .{highlightClass} {{
                    background: rgba(255, 255, 0, 0.25);
                    border: 3px solid {borderColor};
                    border-radius: 4px;
                    padding: 3px;
                }}
                
                .view-label {{
                    position: fixed;
                    top: 20px;
                    right: 20px;
                    background: rgba({labelColor}, 0.9);
                    color: white;
                    padding: 8px 16px;
                    border-radius: 20px;
                    font-size: 14px;
                    font-family: Arial, sans-serif;
                    z-index: 100;
                    pointer-events: none;
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
            <div class='view-label'>{labelText}</div>
            <div class='page-wrapper'>
                <div class='page-container'>
                    <img class='background-img' src='{bgImage}' alt='Background' />
                    <div class='content-overlay'>
                        <div class='base-content'>
                            {baseContent}
                        </div>
                        <div class='highlight-overlay'>
                            {redContent}
                        </div>
                    </div>
                </div>
            </div>
        </body>
        </html>";
    }

    private string GetPlaceholderImage()
    {
        return "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='1024' height='1344'%3E%3Crect width='1024' height='1344' fill='%232d2d44'/%3E%3C/svg%3E";
    }

    private void UpdateViewModeButton()
    {
        string text = _currentViewMode switch
        {
            "content" => "📄 Content",
            "teacher" => "👨‍🏫 Teacher",
            "student" => "👨‍🎓 Student",
            _ => "📄 Content"
        };
        ViewModeButton.Text = text;
        
        ViewModeButton.BackgroundColor = _currentViewMode switch
        {
            "content" => Color.FromArgb("#2c3e50"),
            "teacher" => Color.FromArgb("#3498db"),
            "student" => Color.FromArgb("#2ecc71"),
            _ => Color.FromArgb("#2c3e50")
        };
    }

    private void OnViewModeClicked(object sender, EventArgs e)
    {
        Log("=== VIEW MODE CLICKED ===");
        
        _currentViewMode = _currentViewMode switch
        {
            "content" when (!string.IsNullOrEmpty(_currentTeacherHtml)) => "teacher",
            "teacher" when (!string.IsNullOrEmpty(_currentStudentHtml)) => "student",
            "student" => "content",
            "content" => "content",
            _ => "content"
        };
        
        UpdateViewMode();
    }

    private void UpdateViewMode()
    {
        string html = _currentViewMode switch
        {
            "content" => _currentContentHtml,
            "teacher" => _currentTeacherHtml,
            "student" => _currentStudentHtml,
            _ => _currentContentHtml
        };
        
        if (!string.IsNullOrEmpty(html))
        {
            ContentWebView.Source = new HtmlWebViewSource { Html = html };
            Log($"Switched to view mode: {_currentViewMode}");
        }
        else
        {
            _currentViewMode = "content";
            ContentWebView.Source = new HtmlWebViewSource { Html = _currentContentHtml };
            Log($"View mode {_currentViewMode} was empty, falling back to content");
        }
        
        UpdateViewModeButton();
        
        TeacherNotesButton.BackgroundColor = _currentViewMode == "teacher" 
            ? Color.FromArgb("#e74c3c") 
            : Color.FromArgb("#3498db");
        StudentAnswersButton.BackgroundColor = _currentViewMode == "student" 
            ? Color.FromArgb("#e74c3c") 
            : Color.FromArgb("#2ecc71");
    }

    private void OnTeacherNotesClicked(object sender, EventArgs e)
    {
        Log("=== TEACHER NOTES CLICKED ===");
        if (!string.IsNullOrEmpty(_currentTeacherHtml))
        {
            _currentViewMode = _currentViewMode == "teacher" ? "content" : "teacher";
            UpdateViewMode();
        }
        else
        {
            StatusLabel.Text = "No teacher notes available for this page";
        }
    }

    private void OnStudentAnswersClicked(object sender, EventArgs e)
    {
        Log("=== STUDENT ANSWERS CLICKED ===");
        if (!string.IsNullOrEmpty(_currentStudentHtml))
        {
            _currentViewMode = _currentViewMode == "student" ? "content" : "student";
            UpdateViewMode();
        }
        else
        {
            StatusLabel.Text = "No student answers available for this page";
        }
    }

    private async void OnOpenBookClicked(object sender, EventArgs e)
    {
        Log("=== OPEN BOOK CLICKED ===");
        try
        {
            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Select book.xml"
            });

            if (result != null)
            {
                var filePath = result.FullPath;
                var fileName = Path.GetFileName(filePath);
                Log($"Selected file: {fileName}");

                if (fileName.Equals("book.xml", StringComparison.OrdinalIgnoreCase))
                {
                    var folderPath = Path.GetDirectoryName(filePath);
                    Log($"Book folder: {folderPath}");
                    var success = await _bookService.LoadBookAsync(folderPath);
                    if (success)
                    {
                        TeacherNotesButton.IsEnabled = true;
                        StudentAnswersButton.IsEnabled = true;
                        DecryptButton.IsEnabled = true;
                        SideBySideButton.IsEnabled = true;
                        ViewModeButton.IsEnabled = true;
                        
                        _currentViewMode = "content";
                        UpdateViewModeButton();
                        
                        Log("Book loaded successfully");
                        await DisplayAlert("Success", $"Book loaded!\nLog file: {GetLogFilePath()}", "OK");
                    }
                    else
                    {
                        Log("Failed to load book");
                        await DisplayAlert("Error", "Failed to load book", "OK");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR opening book: {ex.Message}");
            await DisplayAlert("Error", $"Failed to open file: {ex.Message}", "OK");
        }
    }

    private async void OnDownloadClicked(object sender, EventArgs e)
    {
        try
        {
            var number = await DisplayPromptAsync("Download Book",
                "Enter book number to download (e.g., 3688):",
                "Download", "Cancel", "3688", -1, Keyboard.Numeric);

            if (!int.TryParse(number, out int bookNumber))
            {
                await DisplayAlert("Error", "Please enter a valid book number", "OK");
                return;
            }

            StatusLabel.Text = $"Downloading book {bookNumber}...";
            DownloadButton.IsEnabled = false;

            var downloadService = new Services.DownloadService();
            var tcs = new TaskCompletionSource<bool>();
            
            downloadService.OnProgress += (s, progress) =>
            {
                Device.BeginInvokeOnMainThread(() =>
                {
                    StatusLabel.Text = $"Downloading... {progress}%";
                });
            };
            
            downloadService.OnComplete += (s, bookPath) =>
            {
                Device.BeginInvokeOnMainThread(async () =>
                {
                    StatusLabel.Text = $"Book {bookNumber} downloaded successfully!";
                    await DisplayAlert("Success", $"Book {bookNumber} has been downloaded to:\n{bookPath}", "OK");
                    
                    var bookXmlPath = Path.Combine(bookPath, "book.xml");
                    if (File.Exists(bookXmlPath))
                    {
                        await _bookService.LoadBookAsync(bookPath);
                    }
                    
                    DownloadButton.IsEnabled = true;
                    tcs.SetResult(true);
                });
            };
            
            downloadService.OnError += (s, error) =>
            {
                Device.BeginInvokeOnMainThread(async () =>
                {
                    StatusLabel.Text = $"Error: {error}";
                    await DisplayAlert("Error", $"Download failed: {error}", "OK");
                    DownloadButton.IsEnabled = true;
                    tcs.SetResult(false);
                });
            };
            
            await downloadService.DownloadBookAsync(bookNumber);
            await tcs.Task;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
            await DisplayAlert("Error", $"Download failed: {ex.Message}", "OK");
            DownloadButton.IsEnabled = true;
        }
    }

    private async void OnDecryptClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_bookService.CurrentBookPath))
        {
            await DisplayAlert("Info", "Please open a book first.", "OK");
            return;
        }

        var confirm = await DisplayAlert("Confirm Decrypt",
            $"This will decrypt all encrypted files in:\n{_bookService.CurrentBookPath}\n\nContinue?",
            "Yes", "No");

        if (confirm)
        {
            await _bookService.DecryptBookAsync();
            await _bookService.LoadBookAsync(_bookService.CurrentBookPath);
        }
    }

    private void OnPrevClicked(object sender, EventArgs e)
    {
        _bookService.NavigatePrevious();
    }

    private void OnNextClicked(object sender, EventArgs e)
    {
        _bookService.NavigateNext();
    }

    private void OnSideBySideClicked(object sender, EventArgs e)
    {
        _bookService.ToggleSideBySide();
    }

    private void OnZoomInClicked(object sender, EventArgs e)
    {
        _bookService.ZoomIn();
    }

    private void OnZoomOutClicked(object sender, EventArgs e)
    {
        _bookService.ZoomOut();
    }

    private void UpdateUI()
    {
        var total = _bookService.PageFiles.Count;
        var current = _bookService.CurrentPageIndex + 1;

        PrevButton.IsEnabled = _bookService.CurrentPageIndex > 0 && total > 0;
        NextButton.IsEnabled = _bookService.CurrentPageIndex < total - 1 && total > 0;
        PageInfoLabel.Text = total > 0 ? $"{current} / {total}" : "0 / 0";
        ZoomLabel.Text = $"{_bookService.CurrentZoom:F1}x";
    }
}
