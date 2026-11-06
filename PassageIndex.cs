using System.Text.RegularExpressions;

namespace MeetingEvidence;

public static class PassageIndex
{
    public const string Version = "passages-v1-800chars-120overlap";
    public static List<Passage> Build(Corpus corpus)
    {
        var passages = new List<Passage>();
        foreach (var intervention in corpus.Interventions)
        {
            var ordinal = 0;
            foreach (var segment in intervention.Segments)
            {
                var start = 0;
                while (start < segment.Text.Length)
                {
                    var end = Math.Min(start + 800, segment.Text.Length);
                    if (end < segment.Text.Length)
                    {
                        var sentence = Regex.Matches(segment.Text[start..end], @"[.!?][”\""']?\s+")
                            .LastOrDefault(m => m.Index > 400);
                        if (sentence != null) end = start + sentence.Index + sentence.Length;
                        else
                        {
                            var space = segment.Text.LastIndexOf(' ', end - 1, end - start);
                            if (space > start) end = space;
                        }
                    }
                    while (end > start && char.IsWhiteSpace(segment.Text[end - 1])) end--;
                    if (end <= start) break;
                    var text = segment.Text[start..end];
                    passages.Add(new($"{intervention.Id}-p{++ordinal:D3}", intervention.Id, segment.Page, start, end - start, text));
                    if (end == segment.Text.Length) break;
                    var next = Math.Max(start + 1, end - 120);
                    var boundary = segment.Text.IndexOf(' ', next);
                    start = boundary >= next && boundary < end ? boundary + 1 : end;
                    while (start < segment.Text.Length && char.IsWhiteSpace(segment.Text[start])) start++;
                }
            }
        }
        return passages;
    }
}
