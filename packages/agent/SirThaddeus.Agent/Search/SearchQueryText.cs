using System.Text;

namespace SirThaddeus.Agent.Search;

internal static class SearchQueryText
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var input = text.Trim();
        var sb = new StringBuilder(input.Length);
        var lastWasSpace = false;

        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasSpace = false;
                continue;
            }

            if (c is '\'' or '-' or '+')
            {
                sb.Append(c);
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    public static bool IsBannedToken(string tokenLower)
    {
        if (string.IsNullOrWhiteSpace(tokenLower))
            return true;

        if (tokenLower == "thaddeus" || tokenLower.StartsWith("thadd"))
            return true;

        return tokenLower is
            "sir" or "hey" or "hi" or "hello" or "yo" or "sup" or
            "homie" or "buddy" or "pal" or
            "good" or "morning" or "afternoon" or "evening" or
            "well" or "ok" or "okay" or "alright" or "so" or
            "anyway" or "actually" or "basically" or "like" or
            "heck" or "hell" or "gosh" or "gee" or
            "um" or "uh" or "hmm" or "huh" or "er" or "ah" or
            "i" or "im" or "i'm" or "we" or "our" or "us" or
            "you" or "me" or "my" or "he" or "she" or "it" or
            "its" or "it's" or "they" or "them" or "their" or
            "can" or "could" or "would" or "will" or "shall" or
            "should" or "might" or "may" or "do" or "does" or
            "did" or "is" or "are" or "was" or "were" or "been" or
            "being" or "have" or "has" or "had" or
            "want" or "wanted" or "need" or "needed" or "check" or
            "look" or "up" or "search" or "find" or "pull" or
            "show" or "get" or "bring" or "grab" or "fetch" or
            "tell" or "give" or
            "please" or "plz" or "thanks" or "thank" or
            "danke" or "dank" or
            "for" or "to" or "on" or "about" or "into" or "in" or
            "at" or "of" or "with" or "from" or "by" or "or" or
            "and" or "but" or "if" or "then" or "than" or
            "the" or "a" or "an" or "this" or "that" or
            "there" or "here" or "some" or "any" or
            "just" or "really" or "very" or "also" or "too" or
            "what" or "how" or "when" or "where" or "know" or
            "think" or "see" or "go" or "going" or "went";
    }
}