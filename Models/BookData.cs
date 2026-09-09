using System;
using System.Collections.Generic;
using System.Xml;

namespace BookViewer.Models
{
    public class BookData
    {
        public string BookId { get; set; } = "";
        public string Title { get; set; } = "";
        public string CoverPath { get; set; } = "";
        public List<UnitData> Units { get; set; } = new();
        public List<SectionData> AllSections { get; set; } = new();
        public Dictionary<string, List<SectionData>> UnitSections { get; set; } = new();
        public Dictionary<string, List<string>> SectionSteps { get; set; } = new();

        public void LoadFromXml(string xmlContent)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xmlContent);

                var bookNode = doc.SelectSingleNode("//book");
                if (bookNode != null)
                {
                    BookId = bookNode.Attributes?["id"]?.Value ?? "";
                    Title = bookNode.Attributes?["name"]?.Value ?? "";
                    CoverPath = bookNode.Attributes?["coverpath"]?.Value ?? "";
                }

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
                        Units.Add(unit);
                        UnitSections[unit.Id] = new();
                    }
                }

                var sectionDetailNodes = doc.SelectNodes("//sectiondetails/sectiondetail");
                if (sectionDetailNodes != null)
                {
                    foreach (XmlNode node in sectionDetailNodes)
                    {
                        var section = new SectionData
                        {
                            Id = node.Attributes?["id"]?.Value ?? "",
                            Name = node.Attributes?["name"]?.Value ?? ""
                        };
                        AllSections.Add(section);
                        SectionSteps[section.Id] = new();

                        var steps = node.SelectNodes(".//page/@file");
                        if (steps != null)
                        {
                            foreach (XmlNode step in steps)
                            {
                                string stepFile = step.Value;
                                if (!string.IsNullOrEmpty(stepFile))
                                {
                                    SectionSteps[section.Id].Add(stepFile);
                                }
                            }
                        }
                    }
                }

                var unitDetailNodes = doc.SelectNodes("//unitdetail");
                if (unitDetailNodes != null)
                {
                    foreach (XmlNode node in unitDetailNodes)
                    {
                        string unitId = node.Attributes?["id"]?.Value ?? "";
                        if (UnitSections.ContainsKey(unitId))
                        {
                            var sectionNodes = node.SelectNodes(".//section/@id");
                            if (sectionNodes != null)
                            {
                                foreach (XmlNode sectionNode in sectionNodes)
                                {
                                    string sectionId = sectionNode.Value;
                                    var section = AllSections.Find(s => s.Id == sectionId);
                                    if (section != null && !UnitSections[unitId].Contains(section))
                                    {
                                        UnitSections[unitId].Add(section);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to load book data: {ex.Message}");
            }
        }

        public List<SectionData> GetSectionsForUnit(string unitId)
        {
            return UnitSections.TryGetValue(unitId, out var sections) ? sections : new();
        }

        public List<string> GetStepsForSection(string sectionId)
        {
            return SectionSteps.TryGetValue(sectionId, out var steps) ? steps : new();
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
    }
}