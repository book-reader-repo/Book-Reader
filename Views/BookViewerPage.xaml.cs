using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Color = Microsoft.Maui.Graphics.Color;

namespace BookViewer.Views;

public partial class BookViewerPage : ContentPage
{
    private bool _bookLoaded = false;
    private readonly BookService _bookService = new();
    private bool _showTeacherNotes = false;
    private bool _showStudentAnswers = false;
    private string _currentContentHtml = "";
    private string _currentTeacherHtml = "";
    private string _currentStudentHtml = "";
    private string _currentViewMode = "content";

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
            _logFilePath = Path.Combine(logFolder, $"BookViewerPage_{DateTime.Now:yyyyMMdd_HHmmss}.log");
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
        }
    }
    private async void OnBackClicked(object sender, EventArgs e)
    {
        await Navigation.PopAsync();
    }

    public BookViewerPage(string bookFolder, int startPage)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        Log("=== BOOK VIEWER PAGE INITIALIZED ===");
        Log($"Book folder: {bookFolder}");
        Log($"Start page: {startPage}");
        Log($"Log file: {GetLogFilePath()}");

        ContentWebView.BackgroundColor = Colors.Transparent;
        NextPageWebView.BackgroundColor = Colors.Transparent;
        TeacherWebView.BackgroundColor = Colors.Transparent;
        StudentWebView.BackgroundColor = Colors.Transparent;

        _bookService.OnPagesLoaded += (s, pages) =>
        {
            Device.BeginInvokeOnMainThread(() => UpdateUI());
        };

        _bookService.OnPageChanged += (s, content) =>
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                _currentContentHtml = content;
                LoadTeacherView();
                LoadStudentView();
                UpdateDisplay();
                UpdateUI();
            });
        };

        _bookService.OnStatusChanged += (s, msg) =>
            Device.BeginInvokeOnMainThread(() => StatusLabel.Text = msg);

        _bookService.OnBookLoaded += (s, title) =>
            Device.BeginInvokeOnMainThread(() => BookTitleLabel.Text = title);

        _bookService.OnTwoPageSpreadToggled += (s, enabled) =>
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                SideBySideButton.Text = enabled ? "📄 2/2" : "📄 1/2";
                SideBySideButton.BackgroundColor = enabled
                    ? Color.FromArgb("#3498db")
                    : Color.FromArgb("#2c3e50");
                StatusLabel.Text = enabled ? "Two-page spread" : "Single page";
                UpdateDisplay();
            });
        };

        _bookService.OnSideBySideToggled += (s, enabled) =>
        {
            Device.BeginInvokeOnMainThread(() => UpdateDisplay());
        };

        _bookService.OnZoomChanged += (s, zoom) =>
            Device.BeginInvokeOnMainThread(() => ZoomLabel.Text = $"{zoom:F1}x");

        // Load the book, and after pages are loaded, jump to start page
        _bookService.OnPagesLoaded += async (s, pages) =>
        {
            Device.BeginInvokeOnMainThread(async () =>
            {
                if (startPage > 1 && pages.Count > 0)
                {
                    int targetIndex = Math.Min(startPage - 1, pages.Count - 1);
                    if (targetIndex > 0)
                    {
                        Log($"Jumping to start page index {targetIndex}");
                        await _bookService.LoadPageAsync(targetIndex);
                    }
                }
            });
        };

        // Kick off the load
        Loaded += async (s, e) =>
        {
            var success = await _bookService.LoadBookAsync(bookFolder);
            if (success)
            {
                TeacherNotesButton.IsEnabled = true;
                StudentAnswersButton.IsEnabled = true;
                DecryptButton.IsEnabled = true;
                SideBySideButton.IsEnabled = true;
                ViewModeButton.IsEnabled = true;
                ExportPdfButton.IsEnabled = true;
                GridButton.IsEnabled = true;

                _currentViewMode = "content";
                _showTeacherNotes = false;
                _showStudentAnswers = false;
                UpdateViewModeButton();
                UpdateTeacherButton();
                UpdateStudentButton();
                if (startPage > 1)
                {
                    var idx = Math.Min(startPage - 1, _bookService.PageFiles.Count - 1);
                    await _bookService.LoadPageAsync(idx);
                }
            }
            else
            {
                await DisplayAlert("Error", "Failed to load book", "OK");
            }
        };
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

                string baseContent = await GetBaseContentAsync(filePath);
                string redContent = await _bookService.GetRedAnswerContentAsync(filePath, "teacherNotes");
                string bgImage = _bookService.GetStepBackgroundImage(filePath);
                string fontCss = await _bookService.GetFontCssWithEmbeddedFonts(directory);

                _currentTeacherHtml = BuildFullViewHtml(bgImage, baseContent, redContent, fileName, fontCss, "teacherNotes");
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

                string baseContent = await GetBaseContentAsync(filePath);
                string redContent = await _bookService.GetRedAnswerContentAsync(filePath, "studentAnswers");
                string bgImage = _bookService.GetStepBackgroundImage(filePath);
                string fontCss = await _bookService.GetFontCssWithEmbeddedFonts(directory);

                _currentStudentHtml = BuildFullViewHtml(bgImage, baseContent, redContent, fileName, fontCss, "studentAnswers");
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
                    return bodyMatch.Groups[1].Value;
                return oriContent;
            }

            string paraXmlPath = Path.Combine(directory, nameWithoutExt + "_para.xml");
            if (File.Exists(paraXmlPath))
            {
                string paraContent = await File.ReadAllTextAsync(paraXmlPath);
                var doc = new XmlDocument();
                doc.LoadXml(paraContent);
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
            bgImage = GetPlaceholderImage();

        string borderColor = viewType == "teacherNotes" ? "#3498db" : "#2ecc71";
        string highlightClass = viewType == "teacherNotes" ? "tbnote" : "sa";

        if (string.IsNullOrEmpty(redContent))
            return BuildBaseContentHtml(bgImage, baseContent, fileName, fontCss);

        return $@"
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
                .content-overlay > * {{ position: absolute !important; }}
                .base-content {{
                    position: absolute;
                    top: 0; left: 0;
                    width: 100%; height: 100%;
                    z-index: 5;
                    pointer-events: none;
                }}
                .base-content > * {{ position: absolute !important; }}
                .highlight-overlay {{
                    position: absolute;
                    top: 0; left: 0;
                    width: 100%; height: 100%;
                    z-index: 10;
                    pointer-events: none;
                    overflow: visible;
                }}
                .highlight-overlay > *,
                .highlight-overlay > * > * {{
                    position: absolute !important;
                }}
                .{highlightClass} {{
                    background: rgba(255, 255, 0, 0.25);
                    border: 3px solid {borderColor};
                    border-radius: 4px;
                    padding: 3px;
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
                    <div class='base-content'>{baseContent}</div>
                    <div class='highlight-overlay'>{redContent}</div>
                </div>
            </div>
        </body>
        </html>";
    }

    private string BuildBaseContentHtml(string bgImage, string baseContent, string fileName, string fontCss)
    {
        double zoom = _bookService.CurrentZoom;

        if (string.IsNullOrEmpty(bgImage))
            bgImage = GetPlaceholderImage();

        return $@"
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
                .content-overlay > * {{ position: absolute !important; }}
                .base-content {{
                    position: absolute;
                    top: 0; left: 0;
                    width: 100%; height: 100%;
                    z-index: 5;
                    pointer-events: none;
                }}
                .base-content > * {{ position: absolute !important; }}
                ::-webkit-scrollbar {{ width: 6px; height: 6px; }}
                ::-webkit-scrollbar-track {{ background: #1a1a2e; }}
                ::-webkit-scrollbar-thumb {{ background: #2d2d44; border-radius: 3px; }}
            </style>
        </head>
        <body>
            <div class='page-container'>
                <img class='background-img' src='{bgImage}' alt='' />
                <div class='content-overlay'>
                    <div class='base-content'>{baseContent}</div>
                </div>
            </div>
        </body>
        </html>";
    }

    private string GetPlaceholderImage()
    {
        return "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='1024' height='1344'%3E%3Crect width='1024' height='1344' fill='%232d2d44'/%3E%3C/svg%3E";
    }

    private void UpdateDisplay()
    {
        bool isTwoPage = _bookService.TwoPageSpread;

        if (isTwoPage)
        {
            RightColumn.Width = new GridLength(1, GridUnitType.Star);

            ContentWebView.Source = new HtmlWebViewSource { Html = _currentContentHtml };
            ContentWebView.IsVisible = true;

            if (!string.IsNullOrEmpty(_bookService.NextPageHtml))
            {
                NextPageWebView.Source = new HtmlWebViewSource { Html = _bookService.NextPageHtml };
            }
            else
            {
                NextPageWebView.Source = new HtmlWebViewSource { Html = "<html><body style='background:#2d2d44;'></body></html>" };
            }
            NextPageWebView.IsVisible = true;

            TeacherWebView.IsVisible = false;
            StudentWebView.IsVisible = false;
        }
        else
        {
            RightColumn.Width = new GridLength(0);
            NextPageWebView.IsVisible = false;

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
            }
            else
            {
                ContentWebView.Source = new HtmlWebViewSource { Html = _currentContentHtml };
            }

            if (_showTeacherNotes && !string.IsNullOrEmpty(_currentTeacherHtml))
            {
                TeacherWebView.IsVisible = true;
                StudentWebView.IsVisible = false;
                TeacherWebView.Source = new HtmlWebViewSource { Html = _currentTeacherHtml };
            }
            else if (_showStudentAnswers && !string.IsNullOrEmpty(_currentStudentHtml))
            {
                TeacherWebView.IsVisible = false;
                StudentWebView.IsVisible = true;
                StudentWebView.Source = new HtmlWebViewSource { Html = _currentStudentHtml };
            }
            else
            {
                TeacherWebView.IsVisible = false;
                StudentWebView.IsVisible = false;
            }

            UpdateViewModeButton();
        }
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

        TeacherNotesButton.BackgroundColor = _currentViewMode == "teacher"
            ? Color.FromArgb("#e74c3c")
            : Color.FromArgb("#3498db");
        StudentAnswersButton.BackgroundColor = _currentViewMode == "student"
            ? Color.FromArgb("#e74c3c")
            : Color.FromArgb("#2ecc71");
    }

    private void OnViewModeClicked(object sender, EventArgs e)
    {
        _currentViewMode = _currentViewMode switch
        {
            "content" when (!string.IsNullOrEmpty(_currentTeacherHtml)) => "teacher",
            "teacher" when (!string.IsNullOrEmpty(_currentStudentHtml)) => "student",
            "student" => "content",
            "content" => "content",
            _ => "content"
        };

        UpdateDisplay();
        UpdateViewModeButton();
        UpdateTeacherButton();
        UpdateStudentButton();
    }

    private void OnTeacherNotesClicked(object sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentTeacherHtml))
        {
            _currentViewMode = _currentViewMode == "teacher" ? "content" : "teacher";
            UpdateDisplay();
            UpdateViewModeButton();
            UpdateTeacherButton();
            UpdateStudentButton();
        }
        else
        {
            StatusLabel.Text = "No teacher notes available for this page";
        }
    }

    private void OnStudentAnswersClicked(object sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentStudentHtml))
        {
            _currentViewMode = _currentViewMode == "student" ? "content" : "student";
            UpdateDisplay();
            UpdateViewModeButton();
            UpdateTeacherButton();
            UpdateStudentButton();
        }
        else
        {
            StatusLabel.Text = "No student answers available for this page";
        }
    }

    private void UpdateTeacherButton()
    {
        if (TeacherNotesButton != null)
        {
            TeacherNotesButton.Text = _currentViewMode == "teacher" ? "👨‍🏫 Hide" : "👨‍🏫 Notes";
            TeacherNotesButton.BackgroundColor = _currentViewMode == "teacher"
                ? Color.FromArgb("#e74c3c")
                : Color.FromArgb("#3498db");
        }
    }

    private void UpdateStudentButton()
    {
        if (StudentAnswersButton != null)
        {
            StudentAnswersButton.Text = _currentViewMode == "student" ? "👨‍🎓 Hide" : "👨‍🎓 Answers";
            StudentAnswersButton.BackgroundColor = _currentViewMode == "student"
                ? Color.FromArgb("#e74c3c")
                : Color.FromArgb("#2ecc71");
        }
    }

    private async void OnExportPdfClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_bookService.CurrentBookPath))
        {
            await DisplayAlert("Info", "Please load a book first.", "OK");
            return;
        }

        try
        {
            var includeAnswers = await DisplayAlert("Export Options",
                "Include answers in the PDF?",
                "Yes", "No");

            bool includeTeacherNotes = false;
            bool includeStudentAnswers = false;

            if (includeAnswers)
            {
                includeTeacherNotes = await DisplayAlert("Answer Type",
                    "Include Teacher Notes?", "Yes", "No");
                includeStudentAnswers = await DisplayAlert("Answer Type",
                    "Include Student Answers?", "Yes", "No");

                if (!includeTeacherNotes && !includeStudentAnswers)
                    includeAnswers = false;
            }

            var bookTitle = _bookService.BookTitle;
            var safeFileName = string.Join("_", bookTitle.Split(Path.GetInvalidFileNameChars()));
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var exportDir = Path.Combine(documentsPath, "BookViewer", "Exports");
            Directory.CreateDirectory(exportDir);
            var outputPath = Path.Combine(exportDir, $"{safeFileName}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

            ExportPdfButton.IsEnabled = false;
            StatusLabel.Text = "Exporting PDF...";

            var pdfService = new Services.PdfExportService(_bookService);

            pdfService.RenderPageToImageAsync = async (html) =>
            {
#if IOS
                return await BookViewer.Platforms.iOS.WebViewCapture.CaptureHtmlAsync(html, 1024, 1344);
#elif ANDROID
                return await BookViewer.Platforms.Android.WebViewCapture.CaptureHtmlAsync(html, 1024, 1344);
#else
                await Task.CompletedTask;
                return null;
#endif
            };

            pdfService.OnProgress += (s, progress) =>
                Device.BeginInvokeOnMainThread(() => StatusLabel.Text = $"Exporting PDF... {progress}%");

            var tcs = new TaskCompletionSource<string>();
            pdfService.OnComplete += (s, path) => tcs.TrySetResult(path);
            pdfService.OnError += (s, error) => tcs.TrySetException(new Exception(error));

            await pdfService.ExportBookAsPdfAsync(outputPath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

            var resultPath = await tcs.Task;

            ExportPdfButton.IsEnabled = true;
            StatusLabel.Text = "PDF export complete!";

            var openNow = await DisplayAlert("Export Complete",
                $"PDF saved to:\n{resultPath}\n\nOpen it now?",
                "Open", "Later");

            if (openNow)
            {
                await Launcher.Default.OpenAsync(new OpenFileRequest
                {
                    File = new ReadOnlyFile(resultPath)
                });
            }
        }
        catch (Exception ex)
        {
            ExportPdfButton.IsEnabled = true;
            StatusLabel.Text = $"Export failed: {ex.Message}";
            await DisplayAlert("Error", $"Export failed: {ex.Message}", "OK");
        }
    }
    
    private async void OnGridClicked(object sender, EventArgs e)
    {
        if (_bookService.PageFiles.Count == 0) return;
    
        var gridPage = new PageGridView(
            _bookService.PageFiles,
            _bookService.CurrentPageIndex,
            (pageIndex) =>
            {
                // Called after grid pops. Just load the requested page in-place.
                Dispatcher.Dispatch(async () =>
                {
                    await _bookService.LoadPageAsync(pageIndex);
                });
            });
    
        await Navigation.PushAsync(gridPage);
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
    
        if (!confirm) return;
    
        Log("=== DECRYPT START ===");
        StatusLabel.Text = "Decrypting book files...";
    
        // Hook into status changes so decryption progress shows in the status bar
        var progressHandler = new EventHandler<string>((s, msg) =>
        {
            Device.BeginInvokeOnMainThread(() => StatusLabel.Text = msg);
            Log($"Decrypt: {msg}");
        });
        _bookService.OnStatusChanged += progressHandler;
    
        try
        {
            await _bookService.DecryptBookAsync();
            Log("=== DECRYPT COMPLETE ===");
            StatusLabel.Text = "Decryption complete!";
    
            await _bookService.LoadBookAsync(_bookService.CurrentBookPath);
            await DisplayAlert("Success", "Decryption complete!", "OK");
        }
        catch (Exception ex)
        {
            Log($"Decrypt failed: {ex.Message}");
            await DisplayAlert("Error", $"Decryption failed: {ex.Message}", "OK");
        }
        finally
        {
            _bookService.OnStatusChanged -= progressHandler;
        }
    }

    private void OnPrevClicked(object sender, EventArgs e)
    {
        if (_bookService.TwoPageSpread)
        {
            var newIndex = _bookService.CurrentPageIndex - 2;
            if (newIndex >= 0)
                _ = _bookService.LoadPageAsync(newIndex);
            else if (_bookService.CurrentPageIndex > 0)
                _ = _bookService.LoadPageAsync(0);
        }
        else
        {
            _bookService.NavigatePrevious();
        }
    }

    private void OnNextClicked(object sender, EventArgs e)
    {
        if (_bookService.TwoPageSpread)
        {
            var newIndex = _bookService.CurrentPageIndex + 2;
            if (newIndex < _bookService.PageFiles.Count)
                _ = _bookService.LoadPageAsync(newIndex);
            else if (_bookService.CurrentPageIndex < _bookService.PageFiles.Count - 1)
                _ = _bookService.LoadPageAsync(_bookService.PageFiles.Count - 1);
        }
        else
        {
            _bookService.NavigateNext();
        }
    }

    private void OnSideBySideClicked(object sender, EventArgs e)
    {
        _bookService.ToggleTwoPageSpread();
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
