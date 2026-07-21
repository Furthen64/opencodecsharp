namespace OpenCode.Core;

public static class TokenEstimator
{
    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int tokens = 0;
        int i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c <= 0x7F)
            {
                if (char.IsLetter(c))
                {
                    int wordStart = i;
                    while (i < text.Length && char.IsLetter(text[i])) i++;
                    int wordLen = i - wordStart;
                    tokens += Math.Max(1, wordLen / 4);
                }
                else if (char.IsDigit(c))
                {
                    while (i < text.Length && char.IsDigit(text[i])) i++;
                    tokens++;
                }
                else if (c == ' ' || c == '\t')
                {
                    tokens++;
                    i++;
                }
                else if (c == '\n')
                {
                    tokens++;
                    i++;
                }
                else
                {
                    tokens++;
                    i++;
                }
            }
            else
            {
                int byteCount = 0;
                while (i < text.Length && text[i] > 0x7F && byteCount < 4)
                {
                    byteCount++;
                    i++;
                }
                tokens += Math.Max(1, byteCount / 3);
            }
        }

        return tokens;
    }

    public static int EstimateJson(object value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return Estimate(json);
    }
}
