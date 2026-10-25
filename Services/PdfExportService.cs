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

                    html = InjectPdfCss(html, includeTeacherNotes, includeStudentAnswers);
                    
                    html = await ExtractBase64ImagesAsync(html, tempDir, i);

                    var fontProvider = await BuildFontProviderAsync(html, tempDir);
                    var converterProperties = new ConverterProperties();
                    converterProperties.SetFontProvider(fontProvider);
                    converterProperties.SetCreateAcroForm(false);

                    byte[] pageBytes;
                    // FIX: PageSize uses GetHeight() method, not .Height property
                    var pageAnswers = ExtractAnswerPositions(html, pageSize.GetHeight());

                    using (var ms = new MemoryStream())
                    {
                        using (var writer = new PdfWriter(ms))
                        using (var pdf = new PdfDocument(writer))
                        {
                            pdf.SetDefaultPageSize(pageSize);
                            HtmlConverter.ConvertToPdf(html, pdf, converterProperties);

                            if (includeAnswers && pageAnswers.Svgs.Count > 0)
                            {
                                DrawSvgOverlays(pdf, pageAnswers.Svgs);
                            }
                        }
                        pageBytes = ms.ToArray();
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

        private class SvgOverlay
        {
            public string PathD { get; set; } = "";
            public float X { get; set; }
            public float Y { get; set; }
            public float W { get; set; }
            public float H { get; set; }
        }

        private class AnswerPositions
        {
            public List<SvgOverlay> Svgs { get; } = new();
        }

        private AnswerPositions ExtractAnswerPositions(string html, float pageHeight)
        {
            var result = new AnswerPositions();
            
            var svgRegex = new Regex(
                @"<svg\b(?<tag>[^>]*\bclass=[""'][^""']*\btbred\b[^""']*[""'][^>]*)>(?<content>[\s\S]*?)</svg>",
                RegexOptions.IgnoreCase);

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
                    leftPx = float.Parse(styleMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    topPx = float.Parse(styleMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                if (widthMatch.Success) widthPx = float.Parse(widthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                if (heightMatch.Success) heightPx = float.Parse(heightMatch.Groups[1].Value, CultureInfo.InvariantCulture);

                float x = leftPx * 0.75f;
                float y = pageHeight - ((topPx + heightPx) * 0.75f);
                float w = widthPx * 0.75f;
                float h = heightPx * 0.75f;

                result.Svgs.Add(new SvgOverlay { PathD = pathD, X = x, Y = y, W = w, H = h });
            }

            return result;
        }

        private void DrawSvgOverlays(PdfDocument pdf, List<SvgOverlay> svgs)
        {
            if (pdf.GetNumberOfPages() == 0) return;
            var pdfPage = pdf.GetPage(1);

            PdfCanvas canvas = null;
            var bookRed = new DeviceRgb(228, 4, 119);

            foreach (var shape in svgs)
            {
                if (canvas == null)
                {
                    canvas = new PdfCanvas(pdfPage.NewContentStreamAfter(), pdfPage.GetResources(), pdf);
                    canvas.SetStrokeColor(bookRed);
                    canvas.SetLineWidth(1.0f);
                }

                DrawSvgPath(canvas, shape.PathD, shape.X, shape.Y, shape.H);
                canvas.Stroke();
            }

            canvas?.Release();
        }

        private void DrawSvgPath(PdfCanvas canvas, string pathD, float baseX, float baseY, float height)
        {
            try
            {
                var matches = Regex.Matches(pathD, @"([a-zA-Z])|(-?\d*\.?\d+)");
                float currentX = 0, currentY = 0;
                float startX = 0, startY = 0;
                bool hasPoint = false;
                float pt = 0.75f;

                for (int i = 0; i < matches.Count; i++)
                {
                    var token = matches[i].Value;
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

                        while (i + 1 < matches.Count && !char.IsLetter(matches[i + 1].Value[0]))
                        {
                            if (!TryParseFloat(matches[++i].Value, out float p1)) continue;
                            if (i + 1 >= matches.Count || char.IsLetter(matches[i + 1].Value[0])) break;
                            if (!TryParseFloat(matches[++i].Value, out float p2)) continue;

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
                                if (i + 3 >= matches.Count) break;
                                if (!TryParseFloat(matches[++i].Value, out float p3)) continue;
                                if (!TryParseFloat(matches[++i].Value, out float p4)) continue;
                                if (!TryParseFloat(matches[++i].Value, out float p5)) continue;
                                if (!TryParseFloat(matches[++i].Value, out float p6)) continue;
                                
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
                            
                            if (i + 1 >= matches.Count || char.IsLetter(matches[i + 1].Value[0])) break;
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

        private string InjectPdfCss(string html, bool includeTeacherNotes, bool includeStudentAnswers)
        {
            var css = @"
<style>
    @page { size: 768pt 1008pt; margin: 0 !important; }
    html, body { margin: 0 !important; padding: 0 !important; width: 768pt !important; height: 1008pt !important; overflow: visible !important; background: #fff !important; }
    .page-container { position: relative !important; width: 768pt !important; height: 1008pt !important; transform: scale(1) !important; transform-origin: top left !important; margin: 0 !important; }
    img { max-width: 100% !important; height: auto !important; }

    .hideBtn, img.hideBtn, img.redbtn, img.hsgbtn, img.has2btn, svg.tbred { display: none !important; visibility: hidden !important; }
";

            if (includeStudentAnswers || includeTeacherNotes)
            {
                css += @"
    pane.redAnswer { display: block !important; visibility: visible !important; }
";
            }

            if (includeStudentAnswers)
            {
                css += @"
    div.tbred { display: block !important; visibility: visible !important; color: #E40477 !important; }
";
            }

            if (includeTeacherNotes)
            {
                css += @"
    div.tbnote { display: block !important; visibility: visible !important; color: #E40477 !important; }
";
            }

            css += "</style>";

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
