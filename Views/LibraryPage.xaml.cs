using BookViewer.Models;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BookViewer.Views
{
    public partial class LibraryPage : ContentPage
    {
        public class LibraryBook
        {
            public string FolderPath { get; set; } = "";
            public string BookId { get; set; } = "";
            public string Title { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public ImageSource CoverImageSource { get; set; }
            public BookData Data { get; set; }
        }

        private readonly List<LibraryBook> _books = new();

        public LibraryPage()
        {
            InitializeComponent();
            NavigationPage.SetHasNavigationBar(this, false);
            LoadBooks();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            LoadBooks();
        }

        private void LoadBooks()
        {
            _books.Clear();

            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var booksDir = Path.Combine(documentsPath, "BookViewer", "Books");

            if (!Directory.Exists(booksDir))
            {
                Directory.CreateDirectory(booksDir);
                BooksCollection.ItemsSource = _books;
                StatusLabel.Text = "";
                return;
            }

            foreach (var dir in Directory.GetDirectories(booksDir))
            {
                var bookXmlPath = Path.Combine(dir, "book.xml");
                if (!File.Exists(bookXmlPath)) continue;

                try
                {
                    var content = File.ReadAllText(bookXmlPath);
                    var book = new LibraryBook { FolderPath = dir };

                    var data = new BookData();
                    data.LoadFromXml(content, dir);
                    book.Data = data;
                    book.BookId = data.BookId;
                    book.Title = string.IsNullOrEmpty(data.Title) ? Path.GetFileName(dir) : data.Title;
                    book.DisplayName = book.Title;

                    var cover = FindCoverImage(dir, book.BookId);
                    book.CoverImageSource = !string.IsNullOrEmpty(cover) && File.Exists(cover)
                        ? ImageSource.FromFile(cover)
                        : (ImageSource)"appicon.png";

                    _books.Add(book);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error loading {dir}: {ex.Message}");
                }
            }

            BooksCollection.ItemsSource = _books;
            StatusLabel.Text = _books.Count == 0 ? "" : $"{_books.Count}";
        }

        private string FindCoverImage(string dir, string bookId)
        {
            var candidates = new[]
            {
                Path.Combine(dir, $"{bookId}.png"),
                Path.Combine(dir, $"book_{bookId}.png"),
                Path.Combine(dir, "cover.png"),
                Path.Combine(dir, "cover.jpg"),
                Path.Combine(dir, "images", $"book_{bookId}.png"),
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private async void OnBookSelected(object sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not LibraryBook book) return;
            BooksCollection.SelectedItem = null;

            var toc = new TocPage(book.FolderPath, book.Data);
            await Navigation.PushAsync(toc);
        }

        private async void OnBatchDownloadClicked(object sender, EventArgs e)
        {
            // Prompt for a comma/space/newline separated list of book numbers
            var input = await DisplayPromptAsync(
                "Batch Download",
                "Enter book numbers separated by commas, spaces, or new lines.\nExample: 3835, 3836, 3837",
                "Start",
                "Cancel",
                "",
                -1,
                Keyboard.Text);
        
            if (string.IsNullOrWhiteSpace(input))
                return;
        
            // Parse book numbers
            var tokens = input
                .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
                       StringSplitOptions.RemoveEmptyEntries);
        
            var bookNumbers = new List<int>();
            var invalid = new List<string>();
        
            foreach (var t in tokens)
            {
                if (int.TryParse(t.Trim(), out int n) && n > 0)
                    bookNumbers.Add(n);
                else
                    invalid.Add(t.Trim());
            }
        
            // Remove duplicates, keep original order
            bookNumbers = bookNumbers.Distinct().ToList();
        
            if (bookNumbers.Count == 0)
            {
                await DisplayAlert("Batch Download", "No valid book numbers found.", "OK");
                return;
            }
        
            if (invalid.Count > 0)
            {
                var proceed = await DisplayAlert(
                    "Batch Download",
                    $"Skipping invalid entries: {string.Join(", ", invalid)}\n\n" +
                    $"Download {bookNumbers.Count} book(s)?",
                    "Download", "Cancel");
        
                if (!proceed) return;
            }
            else
            {
                var proceed = await DisplayAlert(
                    "Batch Download",
                    $"Download {bookNumbers.Count} book(s)?\n\n{string.Join(", ", bookNumbers)}",
                    "Download", "Cancel");
        
                if (!proceed) return;
            }
        
            // Run the batch
            var booksDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "BookViewer", "Books");
        
            int success = 0;
            int skipped = 0;
            int failed = 0;
            var failures = new List<string>();
        
            var downloadService = new Services.DownloadService();
        
            for (int i = 0; i < bookNumbers.Count; i++)
            {
                var bookNumber = bookNumbers[i];
                var finalDir = Path.Combine(booksDir, $"book_{bookNumber}");
        
                // Skip if already downloaded
                if (Directory.Exists(finalDir))
                {
                    skipped++;
                    Dispatcher.Dispatch(() =>
                        StatusLabel.Text = $"[{i + 1}/{bookNumbers.Count}] book_{bookNumber} already exists, skipping");
                    continue;
                }
        
                // Update status
                var current = i + 1;
                var total = bookNumbers.Count;
                Dispatcher.Dispatch(() =>
                    StatusLabel.Text = $"[{current}/{total}] Downloading book_{bookNumber}...");
        
                var tcs = new TaskCompletionSource<bool>();
        
                EventHandler<int> progressHandler = (s, p) =>
                {
                    Dispatcher.Dispatch(() =>
                        StatusLabel.Text = $"[{current}/{total}] book_{bookNumber}: {p}%");
                };
        
                EventHandler<string> completeHandler = null;
                EventHandler<string> errorHandler = null;
        
                completeHandler = (s, path) =>
                {
                    downloadService.OnProgress -= progressHandler;
                    downloadService.OnComplete -= completeHandler;
                    downloadService.OnError -= errorHandler;
                    tcs.TrySetResult(true);
                };
        
                errorHandler = (s, error) =>
                {
                    downloadService.OnProgress -= progressHandler;
                    downloadService.OnComplete -= completeHandler;
                    downloadService.OnError -= errorHandler;
                    tcs.TrySetResult(false);
                };
        
                downloadService.OnProgress += progressHandler;
                downloadService.OnComplete += completeHandler;
                downloadService.OnError += errorHandler;
        
                try
                {
                    await downloadService.DownloadBookAsync(bookNumber);
                    var ok = await tcs.Task;
        
                    if (ok)
                        success++;
                    else
                    {
                        failed++;
                        failures.Add($"{bookNumber}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add($"{bookNumber}: {ex.Message}");
                }
            }
        
            // Refresh library
            LoadBooks();
        
            // Summary
            var summary = $"Batch download complete.\n\n" +
                          $"Success: {success}\n" +
                          $"Skipped (already exists): {skipped}\n" +
                          $"Failed: {failed}";
        
            if (failures.Count > 0)
                summary += "\n\nFailed books:\n" + string.Join("\n", failures);
        
            StatusLabel.Text = $"{success} downloaded, {skipped} skipped, {failed} failed";
            await DisplayAlert("Batch Download", summary, "OK");
        }

        private async void OnRefreshClicked(object sender, EventArgs e)
        {
            LoadBooks();
            await Task.CompletedTask;
        }

        private async void OnDownloadClicked(object sender, EventArgs e)
        {
            var number = await DisplayPromptAsync("",
                "",
                "OK", "Cancel", "", -1, Keyboard.Numeric);

            if (!int.TryParse(number, out int bookNumber))
                return;

            var downloadService = new Services.DownloadService();
            var tcs = new TaskCompletionSource<bool>();

            StatusLabel.Text = "0%";

            downloadService.OnProgress += (s, progress) =>
            {
                Dispatcher.Dispatch(() => StatusLabel.Text = $"{progress}%");
            };

            downloadService.OnComplete += (s, bookPath) =>
            {
                Dispatcher.Dispatch(() =>
                {
                    LoadBooks();
                    StatusLabel.Text = "";
                });
                tcs.TrySetResult(true);
            };

            downloadService.OnError += (s, error) =>
            {
                Dispatcher.Dispatch(() =>
                {
                    StatusLabel.Text = error;
                });
                tcs.TrySetResult(false);
            };

            await downloadService.DownloadBookAsync(bookNumber);
            await tcs.Task;
        }
    }
}
