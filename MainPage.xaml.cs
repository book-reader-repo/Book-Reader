using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Color = Microsoft.Maui.Graphics.Color;

namespace BookViewer;

public partial class MainPage : ContentPage
{
    private readonly BookService _bookService = new();
    private bool _showTeacherNotes = false;
    private bool _showStudentAnswers = false;
    private string _currentTeacherHtml = "";
    private string _currentStudentHtml = "";

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

        // Make WebViews transparent
        ContentWebView.BackgroundColor = Colors.Transparent;
        TeacherWebView.BackgroundColor = Colors.Transparent;
        StudentWebView.BackgroundColor = Colors.Transparent;

        _bookService.OnPagesLoaded += (s, pages) =>
            Device.BeginInvokeOnMainThread(UpdateUI);

        _bookService.OnPageChanged += (s, content) =>
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                Log($"=== PAGE CHANGED ===");
                Log($"Content length: {content?.Length ?? 0}");
                
                // Main content goes to bottom WebView
                ContentWebView.Source = new HtmlWebViewSource { Html = content };
                
                // Clear overlay WebViews
                TeacherWebView.IsVisible = false;
                StudentWebView.IsVisible = false;
                _currentTeacherHtml = "";
                _currentStudentHtml = "";
                
                // Load teacher notes if available
                LoadTeacherNotes();
                
                // Load student answers if available
                LoadStudentAnswers();
                
                // Apply visibility states
                UpdateOverlayVisibility();
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

    private async void LoadTeacherNotes()
    {
        try
        {
            if (_bookService.CurrentPageIndex >= 0 && _bookService.CurrentPageIndex < _bookService.PageFiles.Count)
            {
                var filePath = _bookService.PageFiles[_bookService.CurrentPageIndex];
                var directory = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileName(filePath) ?? "";
                
                // Get the red answer content only
                string redContent = await _bookService.GetRedAnswerContentAsync(filePath, "teacherNotes");
                
                if (!string.IsNullOrEmpty(redContent))
                {
                    // Get font CSS only (no background image needed for transparent overlay)
                    string fontCss = await _bookService.GetFontCssWithEmbeddedFonts(directory);
                    
                    // Build the HTML for the overlay with transparent background
                    _currentTeacherHtml = BuildOverlayHtml("", redContent, fileName, fontCss);
                    TeacherWebView.Source = new HtmlWebViewSource { Html = _currentTeacherHtml };
                    Log($"Teacher notes loaded, length: {_currentTeacherHtml.Length}");
                }
                else
                {
                    Log("No teacher notes found");
                    _currentTeacherHtml = "";
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading teacher notes: {ex.Message}");
        }
    }
    
    private async void LoadStudentAnswers()
    {
        try
        {
            if (_bookService.CurrentPageIndex >= 0 && _bookService.CurrentPageIndex < _bookService.PageFiles.Count)
            {
                var filePath = _bookService.PageFiles[_bookService.CurrentPageIndex];
                var directory = Path.GetDirectoryName(filePath) ?? "";
                var fileName = Path.GetFileName(filePath) ?? "";
                
                // Get the red answer content only
                string redContent = await _bookService.GetRedAnswerContentAsync(filePath, "studentAnswers");
                
                if (!string.IsNullOrEmpty(redContent))
                {
                    // Get font CSS only (no background image needed for transparent overlay)
                    string fontCss = await _bookService.GetFontCssWithEmbeddedFonts(directory);
                    
                    // Build the HTML for the overlay with transparent background
                    _currentStudentHtml = BuildOverlayHtml("", redContent, fileName, fontCss);
                    StudentWebView.Source = new HtmlWebViewSource { Html = _currentStudentHtml };
                    Log($"Student answers loaded, length: {_currentStudentHtml.Length}");
                }
                else
                {
                    Log("No student answers found");
                    _currentStudentHtml = "";
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Error loading student answers: {ex.Message}");
        }
    }

    private string BuildOverlayHtml(string bgImage, string redContent, string fileName, string fontCss)
    {
        double zoom = _bookService.CurrentZoom;
        
        if (string.IsNullOrEmpty(bgImage))
        {
            bgImage = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='1024' height='1344'%3E%3Crect width='1024' height='1344' fill='transparent'/%3E%3C/svg%3E";
        }

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
                    background: transparent !important;
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
                    background: transparent !important;
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
                    background: transparent !important;
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
                    opacity: 0.01;
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
                
                /* Highlight for teacher notes */
                .tbnote {{
                    background: rgba(255, 255, 0, 0.25);
                    border: 2px solid #3498db;
                    border-radius: 4px;
                    padding: 2px;
                }}
                
                /* Highlight for student answers */
                .sa {{
                    background: rgba(0, 255, 0, 0.25);
                    border: 2px solid #2ecc71;
                    border-radius: 4px;
                    padding: 2px;
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
                        {redContent}
                    </div>
                </div>
            </div>
        </body>
        </html>";
    }

    private void UpdateOverlayVisibility()
    {
        Device.BeginInvokeOnMainThread(() =>
        {
            TeacherWebView.IsVisible = _showTeacherNotes && !string.IsNullOrEmpty(_currentTeacherHtml);
            StudentWebView.IsVisible = _showStudentAnswers && !string.IsNullOrEmpty(_currentStudentHtml);
            
            Log($"Visibility - Teacher: {TeacherWebView.IsVisible}, Student: {StudentWebView.IsVisible}");
        });
    }

    private void UpdateTeacherButton()
    {
        if (TeacherNotesButton != null)
        {
            TeacherNotesButton.Text = _showTeacherNotes ? "👨‍🏫 Hide Notes" : "👨‍🏫 Teacher Notes";
            TeacherNotesButton.BackgroundColor = _showTeacherNotes 
                ? Color.FromArgb("#e74c3c") 
                : Color.FromArgb("#3498db");
            Log($"Teacher button updated: {TeacherNotesButton.Text}");
        }
    }

    private void UpdateStudentButton()
    {
        if (StudentAnswersButton != null)
        {
            StudentAnswersButton.Text = _showStudentAnswers ? "👨‍🎓 Hide Answers" : "👨‍🎓 Student Answers";
            StudentAnswersButton.BackgroundColor = _showStudentAnswers 
                ? Color.FromArgb("#e74c3c") 
                : Color.FromArgb("#2ecc71");
            Log($"Student button updated: {StudentAnswersButton.Text}");
        }
    }

    private void OnTeacherNotesClicked(object sender, EventArgs e)
    {
        Log("=== TEACHER NOTES CLICKED ===");
        _showTeacherNotes = !_showTeacherNotes;
        UpdateTeacherButton();
        
        if (_showTeacherNotes && string.IsNullOrEmpty(_currentTeacherHtml))
        {
            LoadTeacherNotes();
        }
        
        UpdateOverlayVisibility();
    }

    private void OnStudentAnswersClicked(object sender, EventArgs e)
    {
        Log("=== STUDENT ANSWERS CLICKED ===");
        _showStudentAnswers = !_showStudentAnswers;
        UpdateStudentButton();
        
        if (_showStudentAnswers && string.IsNullOrEmpty(_currentStudentHtml))
        {
            LoadStudentAnswers();
        }
        
        UpdateOverlayVisibility();
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
                        
                        _showTeacherNotes = false;
                        _showStudentAnswers = false;
                        UpdateTeacherButton();
                        UpdateStudentButton();
                        
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
