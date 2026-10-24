using iText.Html2pdf;
using iText.Html2pdf.Resolver.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Path = System.IO.Path;

namespace BookViewer.Services
{
    public class PdfExportService
    {
        private readonly BookService _bookService;

        public event EventHandler<int>? OnProgress;
        public event EventHandler<string>? OnComplete;
        public event EventHandler<string>? OnError;

        // Cached once the book font is extracted from @font-face.
        private string? _extractedFontPath;

        public PdfExportService(BookService bookService)
        {
            _bookService = bookService;
        }

        public async Task ExportBookAsPdfAsync(
            string outputPath,
            bool includeAnswers,
            bool includeTeacherNotes,
            bool includeStudentAnswers)
        {
            var tempFiles = new List<string>();
            var tempDir = Path.Combine(Path.GetTempPath(), "BookViewerPdfExport");

            try
            {
                var totalPages = _bookService.PageFiles.Count;
                if (totalPages == 0)
                {
                    OnError?.Invoke(this, "No pages to export");
                    return;
                }

                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
                Directory.CreateDirectory(tempDir);

                var pageSize = new PageSize(768f, 1008f);

                for (int i = 0; i < totalPages; i++)
                {
                    OnProgress?.Invoke(this, (int)((i / (double)totalPages) * 100));

                    var filePath = _bookService.PageFiles[i];

                    string html = await _bookService.BuildPageHtmlForRenderAsync(
                        filePath, includeAnswers, includeTeacherNotes, includeStudentAnswers);

                    html = InjectPdfCss(html);
                    html = await ExtractBase64ImagesAsync(html, tempDir, i);

                    var fontProvider = await BuildFontProviderAsync(html, tempDir);
                    var converterProperties = new ConverterProperties();
                    converterProperties.SetFontProvider(fontProvider);
                    converterProperties.SetCreateAcroForm(false);

                    var tempPdfPath = Path.Combine(tempDir, $"page_{i}.pdf");

                    using (var writer = new PdfWriter(tempPdfPath))
                    using (var pdf = new PdfDocument(writer))
                    {
                        pdf.SetDefaultPageSize(pageSize);
                        HtmlConverter.ConvertToPdf(html, pdf, converterProperties);
                    }

                    tempFiles.Add(tempPdfPath);
                }

                OnProgress?.Invoke(this, 100);

                using (var writer = new PdfWriter(outputPath))
                using (var mergedPdf = new PdfDocument(writer))
                {
                    var merger = new PdfMerger(mergedPdf);
                    foreach (var tempPdf in tempFiles)
                    {
                        using var reader = new PdfReader(tempPdf);
                        using var sourcePdf = new PdfDocument(reader);
                        merger.Merge(sourcePdf, 1, sourcePdf.GetNumberOfPages());
                    }
                }

                OnComplete?.Invoke(this, outputPath);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, ex.Message);
            }
            finally
            {
                foreach (var file in tempFiles)
                {
                    try { File.Delete(file); } catch { }
                }
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        /// <summary>
        /// Extracts the book's embedded font(s) from @font-face in the HTML and
        /// registers them with pdfHTML. Using DefaultFontProvider(false,false,false)
        /// forces pdfHTML to only use our registered fonts, so body text and answer
        /// text (tbred/tbnote) render with the identical font.
        /// </summary>
        private async Task<FontProvider> BuildFontProviderAsync(string html, string tempDir)
        {
            var provider = new DefaultFontProvider(false, false, false);
            bool anyRegistered = false;

            foreach (Match fontFace in Regex.Matches(html, @"@font-face\s*\{[^}]*\}", RegexOptions.IgnoreCase))
            {
                var base64Match = Regex.Match(fontFace.Value, @"base64,([^""']+)\)", RegexOptions.IgnoreCase);
                if (!base64Match.Success) continue;

                try
                {
                    var fontBytes = Convert.FromBase64String(base64Match.Groups[1].Value);

                    // Reuse the extracted font across pages.
                    if (_extractedFontPath == null || !File.Exists(_extractedFontPath))
                    {
                        _extractedFontPath = Path.Combine(tempDir, "book_font.ttf");
                        await File.WriteAllBytesAsync(_extractedFontPath, fontBytes);
                    }

                    provider.AddFont(_extractedFontPath);
                    anyRegistered = true;
                }
                catch
                {
                    // Ignore malformed font blobs; fall back below.
                }

                // One font is enough for consistency; stop after the first success.
                if (anyRegistered) break;
            }

            if (!anyRegistered)
            {
                // No embedded font found — let pdfHTML use standard/system fonts.
                // Note: answer and body text will still share the same fallback.
                provider = new DefaultFontProvider(true, true, true);
            }

            return provider;
        }

        private string InjectPdfCss(string html)
        {
            // NOTE: We intentionally do NOT hide .tbred/.tbnote here. Whether answers
            // appear is controlled by includeAnswers/includeTeacherNotes/includeStudentAnswers
            // in BuildPageHtmlForRenderAsync. Anything that remains is rendered by pdfHTML
            // using the FontProvider we registered, so it matches the body font.
            var css = @"
<style>
    @page { size: 768pt 1008pt; margin: 0 !important; }
    html, body { margin: 0 !important; padding: 0 !important; width: 768pt !important; height: 1008pt !important; overflow: visible !important; background: #fff !important; }
    .page-container { position: relative !important; width: 768pt !important; height: 1008pt !important; transform: scale(1) !important; transform-origin: top left !important; margin: 0 !important; }
    img { max-width: 100% !important; height: auto !important; }
</style>
";
            if (html.Contains("<head>"))
                return html.Replace("<head>", "<head>" + css);
            return css + html;
        }

        private async Task<string> ExtractBase64ImagesAsync(string html, string tempDir, int pageIndex)
        {
            var matches = Regex.Matches(html,
                @"src=""(data:image/([^;]+);base64,([^""]+))""",
                RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                var dataUri = match.Groups[1].Value;
                var ext = match.Groups[2].Value;
                var base64 = match.Groups[3].Value;

                var fileName = $"img_{pageIndex}_{Guid.NewGuid():N}.{ext}";
                var filePath = Path.Combine(tempDir, fileName);

                try
                {
                    await File.WriteAllBytesAsync(filePath, Convert.FromBase64String(base64));
                    html = html.Replace(dataUri, filePath.Replace('\\', '/'));
                }
                catch { }
            }
            return html;
        }
    }
}
