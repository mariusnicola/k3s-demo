using System.Text.RegularExpressions;

namespace BatchTracker.Infrastructure;

/// <summary>Failure messages come from outside this code, so no message reaches the log unfiltered.</summary>
public static class Secrets
{
    public const string Mask = "[redacted]";
    public const int SecretRoom = 8192;

    /// <summary>The engine the mask runs on, which decides what a flood of text costs.</summary>
    public static RegexOptions TokenOptions => Token.Options;

    /// <summary>Replaces each token, key and JWT in the text with the mask.</summary>
    public static string Redact(string text) => Token.Replace(text, Mask);

    /// <summary>
    /// Masks before the final cut, because a cut can leave half a secret the mask no longer matches. The first cut
    /// only bounds the work, and nothing past it is read. A token it splits is masked while `eyJ` plus 16 characters
    /// remain at a word start. A shorter tail stays plain, and reaches the log when masks ahead of it shorten the text
    /// by more than <see cref="SecretRoom"/>. A `Bearer` or key split at the cut stays masked.
    /// </summary>
    public static string MaskAndCut(string text, int max)
    {
        var masked = Redact(text.Length <= max + SecretRoom ? text : text[..(max + SecretRoom)]);
        return masked.Length <= max ? masked : masked[..max];
    }

    /// <summary>Linear in the text: the backtracking engine turns a flood of token starts into minutes. A long run at a word start counts as a token without its dots, because the first cut can remove them. It is case-sensitive so that names are left alone. Limits: the dotted form ignores case and needs no word start, so a name with two dots after `eyj` is masked; a dotless run of under 19 characters stays plain; and `token_eyJ...` has no word start at its `eyJ`, so it is masked only while its dots survive the first cut.</summary>
    private static readonly Regex Token = new(@"Bearer\s+\S+|eyJ[\w-]+\.[\w-]+\.[\w-]*|(?-i:\beyJ[\w-]{16,}(\.[\w-]+)*)|(AccountKey|SharedAccessKey)=[^;\s]+", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
}
