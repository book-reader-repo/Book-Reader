using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace BookViewer.Models
{
    public class BookData
    {
        public string BookId { get; set; } = "";
        public string Title { get; set; } = "";
        public string CoverPath { get; set; } = "";
        public string FolderPath { get; set; } = "";

        public List<UnitData> Units { get; set; } = new();
        public Dictionary<string, List<SectionData>> UnitSections { get; set; } = new();
        public List<SectionData> AllSections { get; set; } = new();

        public Dictionary<int, string> FolioToStepFile { get; set; } = new();

        public void LoadFromXml(string xmlContent, string folderPath)
        {
            FolderPath = folderPath;

            var trimmed = xmlContent.TrimStart();
            if (!trimmed.StartsWith("<book") &&
                !trimmed.StartsWith("<?xml") &&
                !trimmed.StartsWith("<root"))
            {
                xmlContent = $"<root>{xmlContent}</root>";
            }

            var doc = new XmlDocument();
            doc.LoadXml(xmlContent);

            // ---- Book metadata ----
            var bookNode = doc.SelectSingleNode("//book");
            if (bookNode != null)
            {
                BookId = bookNode.Attributes?["id"]?.Value ?? "";
                Title = bookNode.Attributes?["name"]?.Value ?? "";
                CoverPath = bookNode.Attributes?["coverpath"]?.Value ?? "";
            }

            // ---- Units ----
            var unitNodes = doc.SelectNodes("//unit");
            if (unitNodes != null)
            {
                foreach (XmlNode node in unitNodes)
                {
                    var unit = new UnitData
                    {
                        Id = node.Attributes?["id"]?.Value ?? "",
                        Title = node.Attributes?["name"]?.Value ?? ""
                    };

                    if (!Units.Any(u => u.Id == unit.Id))
                    {
                        Units.Add(unit);
                        UnitSections[unit.Id] = new List<SectionData>();
                    }
                }
            }

            // ---- Sections + nested <page> entries ----
            var sectionDetailNodes = doc.SelectNodes("//sectiondetails/sectiondetail");
            if (sectionDetailNodes != null)
            {
                foreach (XmlNode node in sectionDetailNodes)
                {
                    var section = new SectionData
                    {
                        Id = node.Attributes?["id"]?.Value ?? "",
                        Name = node.Attributes?["name"]?.Value ?? "",
                        PageStart = node.Attributes?["pagestart"]?.Value ?? ""
                    };

                    var pageNodes = node.SelectNodes(".//page");
                    if (pageNodes != null)
                    {
                        foreach (XmlNode pageNode in pageNodes)
                        {
                            var file = pageNode.Attributes?["file"]?.Value ?? "";
                            var folioStr = pageNode.Attributes?["folio"]?.Value ?? "";

                            if (!string.IsNullOrEmpty(file))
                                section.StepFiles.Add(file);

                            if (int.TryParse(folioStr, out int folio) && !string.IsNullOrEmpty(file))
                            {
                                if (!FolioToStepFile.ContainsKey(folio))
                                    FolioToStepFile[folio] = file;
                            }
                        }
                    }

                    AllSections.Add(section);
                }
            }

            // ---- Link sections to units ----
            var unitDetailNodes = doc.SelectNodes("//unitdetail");
            if (unitDetailNodes != null)
            {
                foreach (XmlNode node in unitDetailNodes)
                {
                    string unitId = node.Attributes?["id"]?.Value ?? "";
                    if (!UnitSections.ContainsKey(unitId))
                        continue;

                    var sectionNodes = node.SelectNodes(".//section");
                    if (sectionNodes != null)
                    {
                        foreach (XmlNode sectionNode in sectionNodes)
                        {
                            string sectionId = sectionNode.Attributes?["id"]?.Value ?? "";
                            var section = AllSections.FirstOrDefault(s => s.Id == sectionId);
                            if (section != null && !UnitSections[unitId].Contains(section))
                                UnitSections[unitId].Add(section);
                        }
                    }
                }
            }

            // ---- Resources from <sectiongroup> ----
            var sectionGroups = doc.SelectNodes("//sectiongroup");
            if (sectionGroups != null)
            {
                foreach (XmlNode sg in sectionGroups)
                {
                    var desc = sg.Attributes?["desc"]?.Value ?? "";
                    var path = sg.Attributes?["path"]?.Value ?? "";
                    var link = sg.Attributes?["link"]?.Value ?? "";

                    var url = link;
                    if (string.IsNullOrEmpty(url))
                        url = sg.Attributes?["teacherios"]?.Value ?? "";
                    if (string.IsNullOrEmpty(url))
                        url = sg.Attributes?["studentiospath"]?.Value ?? "";
                    if (string.IsNullOrEmpty(url))
                        url = sg.Attributes?["teacherpc"]?.Value ?? "";
                    if (string.IsNullOrEmpty(url))
                        url = sg.Attributes?["studentpc"]?.Value ?? "";

                    if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(desc))
                        continue;

                    var lower = url.ToLower();
                    var type = "link";

                    if (lower.Contains(".mp3") || lower.Contains(".m4a") || lower.Contains(".wav") ||
                        lower.Contains("/audio/") || lower.Contains("_rpa_"))
                        type = "audio";
                    else if (lower.Contains(".mp4") || lower.Contains(".mov") || lower.Contains("video"))
                        type = "video";
                    else if (lower.Contains(".pdf"))
                        type = "pdf";
                    else if (lower.Contains(".docx") || lower.Contains(".doc") ||
                             lower.Contains(".pptx") || lower.Contains(".xlsx"))
                        type = "doc";

                    var resource = new ResourceData
                    {
                        Type = type,
                        Description = Uri.UnescapeDataString(desc),
                        Path = Uri.UnescapeDataString(url),
                        Icon = Uri.UnescapeDataString(path),
                        PageNumber = ""
                    };

                    // Pull page number out of desc like "(第18頁)"
                    var pageMatch = Regex.Match(desc, @"\(第(\d+)頁\)");
                    if (pageMatch.Success)
                        resource.PageNumber = pageMatch.Groups[1].Value;

                    var sectionRefs = sg.SelectNodes(".//sectionset/section");
                    if (sectionRefs != null)
                    {
                        foreach (XmlNode sr in sectionRefs)
                        {
                            var sectionId = sr.Attributes?["id"]?.Value ?? "";
                            if (string.IsNullOrEmpty(sectionId))
                                continue;

                            var section = AllSections.FirstOrDefault(s => s.Id == sectionId);
                            if (section != null)
                                section.Resources.Add(resource);
                        }
                    }
                }
            }
        }

        public List<SectionData> GetSectionsForUnit(string unitId)
        {
            return UnitSections.TryGetValue(unitId, out var s) ? s : new List<SectionData>();
        }
    }

    public class UnitData
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
    }

    public class SectionData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string PageStart { get; set; } = "";
        public List<string> StepFiles { get; set; } = new();
        public List<ResourceData> Resources { get; set; } = new();
    }

    public class ResourceData
    {
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
        public string PageNumber { get; set; } = "";
        public string Path { get; set; } = "";
        public string FallbackUrl { get; set; } = "";   // <-- add this
        public string Icon { get; set; } = "";
    }
}
