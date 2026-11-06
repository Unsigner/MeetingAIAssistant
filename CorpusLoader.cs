using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MeetingEvidence;

public static class CorpusLoader
{
    public const string Version = "oceans-pv-v3";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<Corpus> Load(string root, bool rebuild, ILogger logger)
    {
        var data = Path.Combine(root, "Data");
        var documents = JsonSerializer.Deserialize<List<SourceDocument>>(
            await File.ReadAllTextAsync(Path.Combine(data, "manifest.json")), Json)!;
        var cache = Path.Combine(data, "corpus.json");
        if (!rebuild && File.Exists(cache))
        {
            var saved = JsonSerializer.Deserialize<Corpus>(await File.ReadAllTextAsync(cache), Json);
            if (saved?.ParserVersion == Version && saved.Documents.SequenceEqual(documents) &&
                saved.Status.All(s => s.Status == "indexed") && saved.Status.Count == documents.Count &&
                documents.All(d => File.Exists(Path.Combine(data, "raw", d.FileName))) &&
                documents.All(d => saved.Status.Single(s => s.DocumentId == d.Id).Sha256 ==
                    Hash(Path.Combine(data, "raw", d.FileName)))) return saved;
        }
        var interventions = new List<Intervention>();
        var statuses = new List<DocumentStatus>();
        Directory.CreateDirectory(Path.Combine(data, "extracted"));
        foreach (var doc in documents)
        {
            try
            {
                var path = Path.Combine(data, "raw", doc.FileName);
                using var pdf = PdfDocument.Open(path);
                var lines = new List<SourceLine>();
                var twoColumns = false;
                foreach (var page in pdf.GetPages())
                {
                    var extracted = Extract(page);
                    twoColumns |= extracted.TwoColumns;
                    lines.AddRange(extracted.Lines);
                }
                await File.WriteAllTextAsync(Path.Combine(data, "extracted", doc.Id + ".txt"),
                    string.Join("\n", lines.Select(l => $"[PDF page {l.Page}] {l.Text}")));
                var parsed = Parse(doc, lines);
                if (parsed.Count == 0) throw new InvalidDataException("No oceans interventions recognized. Inspect the extracted text.");
                interventions.AddRange(parsed);
                statuses.Add(new(doc.Id, Hash(path), pdf.NumberOfPages, parsed.Count, "indexed", null, twoColumns));
                logger.LogInformation("Indexed {Symbol}: {Pages} pages, {Count} interventions", doc.Symbol, pdf.NumberOfPages, parsed.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not index {Symbol}", doc.Symbol);
                statuses.Add(new(doc.Id, "", 0, 0, "failed", ex.Message, false));
            }
        }
        var corpus = new Corpus(documents, statuses, interventions, DateTimeOffset.UtcNow, Version);
        await File.WriteAllTextAsync(cache + ".tmp", JsonSerializer.Serialize(corpus, Json));
        File.Move(cache + ".tmp", cache, true);
        return corpus;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // Older PVs use two columns, newer accessible PVs use one. Read each column top to bottom.
    public static (List<SourceLine> Lines, bool TwoColumns) Extract(Page page)
    {
        var height = (double)page.Height;
        var middle = (double)page.Width / 2;
        var allWords = page.GetWords().ToList();
        var footerTop = 35.0;
        if (page.Number == 1)
        {
            // First-page publication notices span both columns and are not speech content.
            var notice = allWords.FirstOrDefault(w => w.Text == "This" && w.BoundingBox.Bottom < 200 &&
                allWords.Any(r => r.Text == "record" && Math.Abs(r.BoundingBox.Bottom - w.BoundingBox.Bottom) < 3 &&
                    r.BoundingBox.Left > w.BoundingBox.Left && r.BoundingBox.Left - w.BoundingBox.Right < 15));
            if (notice != null) footerTop = notice.BoundingBox.Top + 3;
        }
        var words = allWords.Where(w => w.BoundingBox.Bottom > footerTop && w.BoundingBox.Top < height - 35).ToList();
        var body = words.Where(w => w.BoundingBox.Top < height - 155 && w.BoundingBox.Bottom > 55).ToList();
        var spanning = body.Count(w => w.BoundingBox.Left < middle - 3 && w.BoundingBox.Right > middle + 3);
        var isTwo = spanning < 8 && body.Any(w => w.BoundingBox.Left > middle + 10);
        var output = new List<SourceLine>();
        var columns = isTwo
            ? new[] { words.Where(w => w.BoundingBox.Centroid.X < middle), words.Where(w => w.BoundingBox.Centroid.X >= middle) }
            : new[] { words.AsEnumerable() };
        foreach (var column in columns)
        {
            var remaining = column.OrderByDescending(w => w.BoundingBox.Bottom).ToList();
            while (remaining.Count > 0)
            {
                var y = remaining[0].BoundingBox.Bottom;
                var row = remaining.Where(w => Math.Abs(w.BoundingBox.Bottom - y) < 3).OrderBy(w => w.BoundingBox.Left).ToList();
                foreach (var word in row) remaining.Remove(word);
                var text = string.Join(" ", row.Select(w => w.Text));
                if (Regex.IsMatch(text, @"^(A/\d+/PV\.|\d{2}/\d{2}/\d{4}(?:\s|$)|\d+/\d+(?:\s|$)|\d{2}-\d{5}(?:\s|$))")) continue;
                output.Add(new(page.Number, text.Replace('\u00ad', '-')));
            }
        }
        return (output, isTwo);
    }

    public static List<Intervention> Parse(SourceDocument doc, List<SourceLine> lines)
    {
        var result = new List<Intervention>();
        var active = false;
        string? speaker = null, delegation = null;
        var procedural = false;
        var parts = new List<SourceLine>();
        void Flush()
        {
            if (active && speaker != null && parts.Count > 0)
            {
                var segments = parts.GroupBy(p => p.Page).Select(g => new EvidenceSegment(g.Key, Clean(g.Select(x => x.Text)))).ToList();
                var text = string.Join(" ", segments.Select(s => s.Text));
                var group = Regex.Match(text[..Math.Min(text.Length, 700)],
                    @"^(?:On behalf of|I\b[^.!?]{0,240}?(?:on behalf of|speaking for))\s+(?:the\s+)?(?<group>[^.;:]{3,220})", RegexOptions.IgnoreCase);
                var represented = group.Success ? group.Groups["group"].Value.Trim() : null;
                if (represented != null && (represented.Equals(delegation, StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(represented, @"^(?:my|our|this|his|her)\b", RegexOptions.IgnoreCase))) represented = null;
                result.Add(new($"{doc.Id}-{result.Count + 1:D3}", doc.Id, doc.Symbol, doc.Date,
                    speaker, delegation ?? "Unresolved", represented != null ? "Group statement" : "Not identified as a group statement",
                    represented, procedural,
                    parts[0].Page, parts[^1].Page, segments));
            }
            parts = [];
            speaker = delegation = null;
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (Regex.IsMatch(line.Text, @"^Agenda items?\s+\d+(?:\s+and\s+\d+)*(?:\s*\(continued\s*\))?\s*$"))
            {
                Flush();
                active = Regex.IsMatch(line.Text, @"\b75\b");
                continue;
            }
            if (!active) continue;
            var possible = line.Text;
            var consumed = 0;
            // Speaker labels may wrap across several lines, but never absorb a subsequent speech.
            if (Regex.IsMatch(possible, @"^(Mr\.|Mrs\.|Ms\.|The (?:Acting |Temporary )?President\s*:|The Secretary-General\s*:)") )
            {
                while (!possible.Contains(':') && consumed < 5 && i + consumed + 1 < lines.Count)
                    possible += " " + lines[i + ++consumed].Text;
                var match = Regex.Match(possible,
                    @"^(?<name>(?:Mr\.|Mrs\.|Ms\.)\s+[^:()]{1,100})\s*\((?<delegation>[^)]{1,150})\)(?:\s*\([^)]*\))*\s*:\s*(?<text>.*)$");
                var chair = Regex.Match(possible, @"^(?<name>The (?:Acting |Temporary )?President|The Secretary-General)\s*:\s*(?<text>.*)$");
                if (match.Success || chair.Success)
                {
                    Flush();
                    var selected = match.Success ? match : chair;
                    speaker = selected.Groups["name"].Value.Trim();
                    delegation = match.Success ? match.Groups["delegation"].Value.Trim() : selected.Groups["name"].Value;
                    procedural = chair.Success || delegation.Contains("Department for General Assembly");
                    parts.Add(new(line.Page, selected.Groups["text"].Value));
                    i += consumed;
                    continue;
                }
                // Unknown speaker-looking labels terminate attribution rather than leaking the previous identity.
                if (Regex.IsMatch(possible, @"^(?:Mr\.|Mrs\.|Ms\.)\s+[^:()]{1,100}\([^)]{1,150}\).*?:"))
                {
                    Flush();
                    speaker = "Unresolved speaker";
                    delegation = "Unresolved";
                }
            }
            if (speaker != null) parts.Add(line);
        }
        Flush();
        return result;
    }

    private static string Clean(IEnumerable<string> lines) =>
        Regex.Replace(string.Join(" ", lines), @"\s+", " ").Trim();
}
