using System;
using System.Collections.Generic;
using System.Text;

namespace LiveMeetAI.Core
{
    public class TranscriptManager
    {
        private readonly List<(DateTime t, string text)> items = new List<(DateTime, string)>();

        public void Add(string text)
        {
            items.Add((DateTime.Now, text));
        }

        public string GetFullTranscript()
        {
            var sb = new StringBuilder();
            foreach (var it in items)
            {
                sb.AppendLine($"[{it.t:yyyy-MM-dd HH:mm:ss}] {it.text}");
            }
            return sb.ToString();
        }
    }
}
