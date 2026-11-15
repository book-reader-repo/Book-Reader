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
            public string BookIdLabel { get; set; } = "";
            public ImageSource CoverImageSource { get; set; }
            public BookData Data { get; set; }
        }

        private readonly List<LibraryBook> _allBooks = new();
        private readonly List<LibraryBook> _books = new();

        // Used to suppress the tap that follows a long-press.
        private LibraryBook _longPressedBook;
        private DateTime _longPressTime;

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
            _allBooks.Clear();

            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var booksDir = Path.Combine(documentsPath, "BookViewer", "Books");

            if (!Directory.Exists(booksDir))
            {
                Directory.CreateDirectory(booksDir);
                ApplyFilter();
                return;
            }

            foreach (var dir in Directory.GetDirectories(booksDir))
            {
                // Skip temp / resource folders
                var name = Path.GetFileName(dir);
                if (name.StartsWith("temp_", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("resources", StringComparison.OrdinalIgnoreCase)) continue;

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
                    book.Title = string.IsNullOrEmpty(data.Title)
                        ? Path.GetFileName(dir)
                        : data.Title;
                    book.DisplayName = book.Title;
                    book.BookIdLabel = string.IsNullOrEmpty(book.BookId)
                        ? Path.GetFileName(dir)
                        : $"#{book.BookId}";

                    var cover = FindCoverImage(dir, book.BookId);
                    book.CoverImageSource = !string.IsNullOrEmpty(cover) && File.Exists(cover)
                        ? ImageSource.FromFile(cover)
                        : (ImageSource)"appicon.png";

                    _allBooks.Add(book);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error loading {dir}: {ex.Message}");
                }
            }

            _allBooks.Sort((a, b) =>
            {
                bool an = int.TryParse(a.BookId, out int ai);
                bool bn = int.TryParse(b.BookId, out int bi);
                if (an && bn) return ai.CompareTo(bi);
                if (an) return -1;
                if (bn) return 1;
                return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var query = SearchEntry?.Text?.Trim() ?? "";

            _books.Clear();

            if (string.IsNullOrEmpty(query))
            {
                _books.AddRange(_allBooks);
            }
            else
            {
                foreach (var b in _allBooks)
                {
                    if ((b.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (b.BookId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (Path.GetFileName(b.FolderPath)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        _books.Add(b);
                    }
                }
            }

            BooksCollection.ItemsSource = null;
            BooksCollection.ItemsSource = _books;

            if (StatusLabel != null)
            {
                if (string.IsNullOrEmpty(query))
                    StatusLabel.Text = _allBooks.Count == 0 ? "" : $"{_allBooks.Count}";
                else
                    StatusLabel.Text = $"{_books.Count} / {_allBooks.Count}";
            }
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void OnClearSearchClicked(object sender, EventArgs e)
        {
            if (SearchEntry != null)
            {
                SearchEntry.Text = "";
                ApplyFilter();
            }
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

        private async void OnBookTapped(object sender, TappedEventArgs e)
        {
            if ((sender as BindableObject)?.BindingContext is not LibraryBook book)
                return;

            // Suppress tap that immediately follows a long-press on the same book.
            if (_longPressedBook == book &&
                (DateTime.UtcNow - _longPressTime).TotalMilliseconds < 1000)
            {
                _longPressedBook = null;
                return;
            }

            try
            {
                var toc = new TocPage(book.FolderPath, book.Data);
                await Navigation.PushAsync(toc);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }

        private async void OnBookLongPressed(object sender, EventArgs e)
        {
            if (sender is not BookViewer.Behaviors.LongPressBehavior behavior)
                return;

            if (behavior.CommandParameter is not LibraryBook book)
                return;

            _longPressedBook = book;
            _longPressTime = DateTime.UtcNow;

            await ConfirmDeleteBookAsync(book);
        }

        private async Task ConfirmDeleteBookAsync(LibraryBook book)
        {
            if (book == null) return;

            var confirm = await DisplayAlert(
                "Remove Book",
                $"Delete \"{book.Title}\" (book_{book.BookId}) from your library?\n\nThis removes the downloaded files from disk.",
                "Delete", "Cancel");

            if (!confirm) return;

            try
            {
                // Drop the cover image reference so the file handle is released
                book.CoverImageSource = null;

                // Rebind the collection so the Image handler is detached before deletion
                BooksCollection.ItemsSource = null;

                GC.Collect();
                GC.WaitForPendingFinalizers();

                // Small delay for OS to release any lingering file handles
                await Task.Delay(150);

                if (Directory.Exists(book.FolderPath))
                    Directory.Delete(book.FolderPath, true);

                _allBooks.Remove(book);
                _books.Remove(book);

                ApplyFilter();

                StatusLabel.Text = $"Removed book_{book.BookId}";
            }
            catch (Exception ex)
            {
                await DisplayAlert("Delete Failed",
                    $"Could not delete the book folder:\n{ex.Message}\n\n{book.FolderPath}", "OK");
                // Restore the list on failure
                ApplyFilter();
            }
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

        private async void OnBatchDownloadClicked(object sender, EventArgs e)
        {
            var startInput = await DisplayPromptAsync(
                "Batch Download",
                "Start book number:",
                "Next",
                "Cancel",
                "",
                -1,
                Keyboard.Numeric);

            if (string.IsNullOrWhiteSpace(startInput))
                return;

            if (!int.TryParse(startInput.Trim(), out int startNum) || startNum <= 0)
            {
                await DisplayAlert("Batch Download", "Invalid start number.", "OK");
                return;
            }

            var endInput = await DisplayPromptAsync(
                "Batch Download",
                $"End book number (start: {startNum}):",
                "Download",
                "Cancel",
                "",
                -1,
                Keyboard.Numeric);

            if (string.IsNullOrWhiteSpace(endInput))
                return;

            if (!int.TryParse(endInput.Trim(), out int endNum) || endNum <= 0)
            {
                await DisplayAlert("Batch Download", "Invalid end number.", "OK");
                return;
            }

            if (endNum < startNum)
            {
                await DisplayAlert("Batch Download",
                    "End number must be greater than or equal to start number.", "OK");
                return;
            }

            const int MAX_RANGE = 200;
            if (endNum - startNum + 1 > MAX_RANGE)
            {
                await DisplayAlert("Batch Download",
                    $"Range too large (max {MAX_RANGE} books at a time).", "OK");
                return;
            }

            var bookNumbers = new List<int>();
            for (int n = startNum; n <= endNum; n++)
                bookNumbers.Add(n);

            var proceed = await DisplayAlert(
                "Batch Download",
                $"Download {bookNumbers.Count} book(s):\n\n{startNum} → {endNum}",
                "Download", "Cancel");

            if (!proceed) return;

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

                if (Directory.Exists(finalDir))
                {
                    skipped++;
                    Dispatcher.Dispatch(() =>
                        StatusLabel.Text = $"[{i + 1}/{bookNumbers.Count}] book_{bookNumber} already exists, skipping");
                    continue;
                }

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
                        failures.Add(bookNumber.ToString());
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    failures.Add($"{bookNumber}: {ex.Message}");
                }
            }

            LoadBooks();

            var summary = $"Batch download complete.\n\n" +
                          $"Success: {success}\n" +
                          $"Skipped (already exists): {skipped}\n" +
                          $"Failed: {failed}";

            if (failures.Count > 0)
                summary += "\n\nFailed books:\n" + string.Join("\n", failures);

            StatusLabel.Text = $"{success} downloaded, {skipped} skipped, {failed} failed";
            await DisplayAlert("Batch Download", summary, "OK");
        }
    }
}
