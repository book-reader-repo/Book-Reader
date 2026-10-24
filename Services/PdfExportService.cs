using iText.Html2pdf;
using iText.Html2pdf.Resolver.Font;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Utils;
using iText.Layout.Font;
using System;
using System.Collections.Generic;
using System.Globalization;
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
                var pdfStreams = new List<byte[]>();

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

                    byte[] pageBytes;

                    // 1. Convert HTML to PDF in memory
                    using (var ms = new MemoryStream())
                    {
                        using (var writer = new PdfWriter(ms))
                        using (var pdf = new PdfDocument(writer))
                        {
                            pdf.SetDefaultPageSize(pageSize);
                            HtmlConverter.ConvertToPdf(html, pdf, converterProperties);
                        }
                        pageBytes = ms.ToArray();
                    }

                    // 2. Draw SVG overlays (if needed) using AppendMode
                    if (includeAnswers)
                    {
                        using (var readMs = new MemoryStream(pageBytes))
                        using (var writeMs = new MemoryStream())
                        {
                            using (var reader = new PdfReader(readMs))
                            using (var writer = new PdfWriter(writeMs))
                            using (var pdf = new PdfDocument(reader, writer, new StampingProperties().UseAppendMode()))
                            {
                                DrawSvgOverlays(pdf, html);
                            }
                            pageBytes = writeMs.ToArray();
                        }
                    }

                    pdfStreams.Add(pageBytes);
                }

                OnProgress?.Invoke(this, 100);

                using (var writer = new PdfWriter(outputPath))
                using (var mergedPdf = new PdfDocument(writer))
                {
                    var merger = new PdfMerger(mergedPdf);
                    foreach (var pdfBytes in pdfStreams)
                    {
                        using var ms = new MemoryStream(pdfBytes);
                        using var reader = new PdfReader(ms);
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
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        private void DrawSvgOverlays(PdfDocument pdf, string html)
        {
            if (pdf.GetNumberOfPages() == 0) return;
            var pdfPage = pdf.GetPage(1);
            float pageHeight = pdfPage.GetPageSize().GetHeight();

            var svgRegex = new Regex(
                @"<svg\b(?<tag>[^>]*\bclass=[""'][^""']*\btbred\b[^""']*[""'][^>]*)>(?<content>[\s\S]*?)</svg>",
                RegexOptions.IgnoreCase);

            PdfCanvas canvas = null;
            bool hasDrawn = false;

            foreach (Match sm in svgRegex.Matches(html))
            {
                string tag = sm.Groups["tag"].Value;
                string content = sm.Groups["content"].Value;

                var pathMatch = Regex.Match(content, @"<path\b[^>]*\bd=[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (!pathMatch.Success) continue;
                string pathD = pathMatch.Groups[1].Value;

                float leftPx = 0f, topPx = 0f, widthPx = 82f, heightPx = 32f;
                var styleMatch = Regex.Match(tag, @"style=[""'][^""']*left:\s*([\d\.]+)px[^""']*top:\s*([\d\.]+)px", RegexOptions.IgnoreCase);
                var widthMatch = Regex.Match(tag, @"\bwidth=[""'](\d+)[""']", RegexOptions.IgnoreCase);
                var heightMatch = Regex.Match(tag, @"\bheight=[""'](\d+)[""']", RegexOptions.IgnoreCase);

                if (styleMatch.Success)
                {
                    if (float.TryParse(styleMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float parsedLeft)) leftPx = parsedLeft;
                    if (float.TryParse(styleMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float parsedTop)) topPx = parsedTop;
                }
                if (widthMatch.Success && float.TryParse(widthMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float parsedWidth)) widthPx = parsedWidth;
                if (heightMatch.Success && float.TryParse(heightMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float parsedHeight)) heightPx = parsedHeight;

                float x = leftPx * 0.75f;
                float y = pageHeight - ((topPx + heightPx) * 0.75f);

                if (canvas == null)
                {
                    canvas = new PdfCanvas(pdfPage.NewContentStreamAfter(), pdfPage.GetResources(), pdf);
                    canvas.SetStrokeColor(new DeviceRgb(228, 4, 119)); // Exact red
                    canvas.SetLineWidth(1.0f);
                }
                
                DrawSvgPath(canvas, pathD, x, y, heightPx * 0.75f);
                hasDrawn = true;
            }

            if (hasDrawn && canvas != null)
            {
                canvas.Stroke();
            }
            canvas?.Release();
        }

        private void DrawSvgPath(PdfCanvas canvas, string pathD, float baseX, float baseY, float height)
        {
            try
            {
                // Split tokens by spaces or commas
                var tokens = pathD.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                float currentX = 0, currentY = 0;
                float startX = 0, startY = 0;
                bool hasPoint = false;
                float pt = 0.75f;

                for (int i = 0; i < tokens.Length; i++)
                {
                    var token = tokens[i];
                    if (string.IsNullOrWhiteSpace(token)) continue;

                    if (char.IsLetter(token[0]))
                    {
                        char cmd = token[0];
                        bool isRel = char.IsLower(cmd);
                        char upperCmd = char.ToUpper(cmd);

                        if (upperCmd == 'Z')
                        {
                            canvas.ClosePath();
                            currentX = startX; currentY = startY;
                            continue;
                        }

                        // Read numbers until the next command letter
                        while (i + 1 < tokens.Length && !char.IsLetter(tokens[i + 1][0]))
                        {
                            if (!TryParseFloat(tokens[++i], out float p1)) continue;
                            if (i + 1 >= tokens.Length || char.IsLetter(tokens[i + 1][0])) break;
                            if (!TryParseFloat(tokens[++i], out float p2)) continue;

                            if (upperCmd == 'M')
                            {
                                if (!hasPoint) { startX = p1; startY = p2; hasPoint = true; }
                                if (!isRel) { currentX = p1; currentY = p2; }
                                else { currentX += p1; currentY += p2; }
                                
                                canvas.MoveTo(baseX + currentX * pt, baseY + height - (currentY * pt));
                                upperCmd = 'L'; 
                            }
                            else if (upperCmd == 'L')
                            {
                                if (!isRel) { currentX = p1; currentY = p2; }
                                else { currentX += p1; currentY += p2; }
                                canvas.LineTo(baseX + currentX * pt, baseY + height - (currentY * pt));
                            }
                            else if (upperCmd == 'C')
                            {
                                if (i + 3 >= tokens.Length) break;
                                if (!TryParseFloat(tokens[++i], out float p3)) continue;
                                if (!TryParseFloat(tokens[++i], out float p4)) continue;
                                if (!TryParseFloat(tokens[++i], out float p5)) continue;
                                if (!TryParseFloat(tokens[++i], out float p6)) continue;
                                
                                float x1 = p1, y1 = p2, x2 = p3, y2 = p4, x3 = p5, y3 = p6;
                                if (isRel)
                                {
                                    x1 += currentX; y1 += currentY;
                                    x2 += currentX; y2 += currentY;
                                    x3 += currentX; y3 += currentY;
                                }
                                
                                canvas.CurveTo(
                                    baseX + x1 * pt, baseY + height - (y1 * pt),
                                    baseX + x2 * pt, baseY + height - (y2 * pt),
                                    baseX + x3 * pt, baseY + height - (y3 * pt)
                                );
                                currentX = x3; currentY = y3;
                            }
                            
                            if (i + 1 >= tokens.Length || char.IsLetter(tokens[i + 1][0])) break;
                        }
                    }
                }
            }
            catch { }
        }

        private bool TryParseFloat(string s, out float result)
        {
            return float.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
        }

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

                    if (_extractedFontPath == null || !File.Exists(_extractedFontPath))
                    {
                        _extractedFontPath = Path.Combine(tempDir, "book_font.ttf");
                        await File.WriteAllBytesAsync(_extractedFontPath, fontBytes);
                    }

                    provider.AddFont(_extractedFontPath);
                    anyRegistered = true;
                }
                catch { }

                if (anyRegistered) break;
            }

            if (!anyRegistered)
            {
                provider = new DefaultFontProvider(true, true, true);
            }

            return provider;
        }

        private string InjectPdfCss(string html)
        {
            var css = @"
<style>
    @page { size: 768pt 1008pt; margin: 0 !important; }
    html, body { margin: 0 !important; padding: 0 !important; width: 768pt !important; height: 1008pt !important; overflow: visible !important; background: #fff !important; }
    .page-container { position: relative !important; width: 768pt !important; height: 1008pt !important; transform: scale(1) !important; transform-origin: top left !important; margin: 0 !important; }
    img { max-width: 100% !important; height: auto !important; }

    /* FORCE ANSWERS TO BE VISIBLE IN PDF */
    pane.redAnswer { display: block !important; visibility: visible !important; }
    div.tbred, div.tbnote { display: block !important; visibility: visible !important; color: #E40477 !important; }
    
    /* HIDE INTERACTIVE BUTTONS AND SVGS FROM PDFHTML (SVGS ARE DRAWN MANUALLY) */
    .hideBtn, img.hideBtn, img.redbtn, img.hsgbtn, img.has2btn, svg.tbred { display: none !important; visibility: hidden !important; }
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
