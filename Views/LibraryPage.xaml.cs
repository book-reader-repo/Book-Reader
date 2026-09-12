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
