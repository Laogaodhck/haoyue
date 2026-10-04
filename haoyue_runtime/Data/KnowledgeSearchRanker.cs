using System.Text;

namespace Haoyue.Runtime.Data;

/// <summary>
/// One query token after normalization, synonym expansion and CJK bigram slicing.
/// ExactVariants are matched with plain substring Contains; FuzzyVariants (whole
/// words only, no bigrams) are used by the edit-distance fallback when no exact
/// candidate matched at all.
/// </summary>
public sealed record KnowledgeQueryToken(IReadOnlyList<string> ExactVariants, IReadOnlyList<string> FuzzyVariants);

/// <summary>
/// Query-side ranking for KnowledgeStore.Search, tuned for mixed Chinese/English
/// knowledge bases. Substring matching stays the workhorse (SQLite tokenizers do
/// not segment CJK, see KnowledgeStore), but the raw user query is preprocessed
/// before it is scored:
///   1. Normalization (FormKC folds full-width punctuation/letters to ASCII, then
///      lowercases) applied identically to the query and to entry text.
///   2. CJK bigram slicing: an unspaced Chinese question like "数据库连接怎么配置"
///      is no longer one long term that must appear verbatim; its 2-char slices
///      can match entry substrings independently.
///   3. Synonym expansion for common technical terms ("编译" also finds entries
///      that only say "构建/打包", "登陆" also finds "登录").
///   4. Coverage-first ranking: entries matching more of the user's tokens rank
///      above entries that hit one token many times.
///   5. Edit-distance fallback on title/tags when nothing matched exactly, so a
///      single typo ("pnpm" typed as "puipm") still surfaces the right entry.
/// Entry scoring weights are unchanged from the legacy algorithm (title +3,
/// content +2, tags +2, counted once per token per field), so single-keyword
/// queries keep their old relative order.
/// </summary>
public static class KnowledgeSearchRanker
{
    // Query-side synonym table. Keys and values are already normalized (lowercase,
    // half-width). Kept deliberately small and high-precision; rare synonyms add
    // noise, not recall.
    private static readonly Dictionary<string, string[]> Synonyms = new()
    {
        ["部署"] = ["发布", "上线"],
        ["发布"] = ["部署", "上线"],
        ["上线"] = ["部署", "发布"],
        ["安装"] = ["安装包", "部署"],
        ["打包"] = ["构建", "编译"],
        ["构建"] = ["打包", "编译"],
        ["编译"] = ["构建", "打包"],
        ["登录"] = ["登陆", "登入"],
        ["登陆"] = ["登录", "登入"],
        ["登入"] = ["登录", "登陆"],
        ["账号"] = ["帐号", "账户"],
        ["帐号"] = ["账号", "账户"],
        ["配置"] = ["设置"],
        ["设置"] = ["配置"],
        ["报错"] = ["出错", "异常", "失败"],
        ["出错"] = ["报错", "异常"],
        ["异常"] = ["报错", "出错"],
        ["失败"] = ["报错", "出错"],
        ["崩溃"] = ["闪退"],
        ["闪退"] = ["崩溃"],
        ["卡顿"] = ["卡住", "卡死"],
        ["文档"] = ["说明", "手册"],
        ["命令"] = ["指令"],
        ["端口"] = ["port"],
        ["端口占用"] = ["端口被占用"],
        ["密钥"] = ["key"],
        ["乱码"] = ["编码"],
        ["编码"] = ["乱码"],
    };

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        // FormKC folds full-width forms (ＰＹＴＨＯＮ，：？) into ASCII equivalents;
        // CJK ideographs are untouched. Lowercasing replaces the old
        // OrdinalIgnoreCase Contains because both sides go through the same path.
        return text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    }

    public static bool ContainsCjk(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF')
                return true;
        }
        return false;
    }

    /// <summary>Normalizes the query, expands synonyms and slices CJK words into bigrams.</summary>
    public static IReadOnlyList<KnowledgeQueryToken> BuildTokens(string? query)
    {
        var normalized = Normalize(query);
        if (normalized.Length == 0) return [];

        var rawTokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokens = new List<KnowledgeQueryToken>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in rawTokens)
        {
            var exact = new List<string>();
            var fuzzy = new List<string>();

            // The raw word plus its synonyms are matched as whole strings…
            var words = new List<string> { raw };
            if (Synonyms.TryGetValue(raw, out var synonyms)) words.AddRange(synonyms);

            foreach (var word in words)
            {
                if (word.Length == 0) continue;
                if (seen.Add("e:" + word)) exact.Add(word);
                if (word.Length >= 2 && word.Length <= 8 && seen.Add("f:" + word)) fuzzy.Add(word);

                // …while multi-char CJK words are additionally sliced into
                // sliding 2-char bigrams so partial question phrasing ("怎么",
                // "如何" interrupting) still matches entry substrings. Bigrams
                // also get synonym expansion — "登入失败" slices into "登入",
                // which then also matches "登录" titles with title weight.
                if (word.Length > 2 && ContainsCjk(word))
                {
                    for (var i = 0; i + 2 <= word.Length; i++)
                    {
                        var gram = word.Substring(i, 2);
                        if (seen.Add("e:" + gram)) exact.Add(gram);
                        if (Synonyms.TryGetValue(gram, out var gramSynonyms))
                        {
                            foreach (var synonym in gramSynonyms)
                                if (seen.Add("e:" + synonym)) exact.Add(synonym);
                        }
                    }
                }
            }

            if (exact.Count > 0) tokens.Add(new KnowledgeQueryToken(exact, fuzzy));
        }
        return tokens;
    }

    /// <summary>
    /// Scores one entry against the tokens: title +3, content +2, tags +2, counted
    /// once per token per field no matter how many of its variants matched (keeps
    /// bigrams and synonyms from inflating the score). Returns the score and how
    /// many tokens were covered at all.
    /// </summary>
    public static (int Score, int Covered) ScoreEntry(
        string title, string content, string tags, IReadOnlyList<KnowledgeQueryToken> tokens)
    {
        var score = 0;
        var covered = 0;
        foreach (var token in tokens)
        {
            var inTitle = false;
            var inContent = false;
            var inTags = false;
            foreach (var variant in token.ExactVariants)
            {
                if (!inTitle && title.Contains(variant, StringComparison.Ordinal)) inTitle = true;
                if (!inContent && content.Contains(variant, StringComparison.Ordinal)) inContent = true;
                if (!inTags && tags.Length > 0 && tags.Contains(variant, StringComparison.Ordinal)) inTags = true;
            }
            var tokenScore = (inTitle ? 3 : 0) + (inContent ? 2 : 0) + (inTags ? 2 : 0);
            if (tokenScore > 0)
            {
                score += tokenScore;
                covered++;
            }
        }
        return (score, covered);
    }

    /// <summary>
    /// Ranks entries for a query. Exact matches (if any) win; otherwise a fuzzy
    /// pass over title/tags gives typo tolerance. Ordering: token coverage first,
    /// then score, then recency — identical to the legacy order for single-token
    /// queries.
    /// </summary>
    public static IReadOnlyList<KnowledgeEntry> Rank(IEnumerable<KnowledgeEntry> entries, string? query, int limit)
    {
        var tokens = BuildTokens(query);
        if (tokens.Count == 0) return [];

        var scored = new List<(KnowledgeEntry Entry, int Score, int Covered, double Coverage)>();
        foreach (var entry in entries)
        {
            var title = Normalize(entry.Title);
            var content = Normalize(entry.Content);
            var tags = Normalize(entry.Tags);
            var (score, covered) = ScoreEntry(title, content, tags, tokens);
            if (score > 0)
                scored.Add((entry, score, covered, (double)covered / tokens.Count));
        }

        if (scored.Count == 0)
        {
            // Nothing matched verbatim — fall back to edit-distance matching on
            // short fields so one typo does not blank the result set. Bigrams are
            // excluded from fuzzy matching (too small to be meaningful).
            foreach (var entry in entries)
            {
                var title = Normalize(entry.Title);
                var tags = Normalize(entry.Tags);
                var score = 0;
                var covered = 0;
                foreach (var token in tokens)
                {
                    var hit = token.FuzzyVariants.Any(variant =>
                        FuzzyContains(title, variant) ||
                        (tags.Length > 0 && FuzzyContains(tags, variant)));
                    if (hit)
                    {
                        score += 1;
                        covered++;
                    }
                }
                if (score > 0)
                    scored.Add((entry, score, covered, (double)covered / tokens.Count));
            }
        }

        return scored
            .OrderByDescending(c => c.Coverage)
            .ThenByDescending(c => c.Score)
            .ThenByDescending(c => c.Entry.UpdatedAt, StringComparer.Ordinal)
            .Take(limit)
            .Select(c => c.Entry)
            .ToList();
    }

    /// <summary>
    /// True when any substring of text is within maxDistance edits of pattern
    /// (classic rolled-row substring edit distance). Used only on title/tags,
    /// which are short enough for the O(len*len) scan.
    /// </summary>
    public static bool FuzzyContains(string text, string pattern, int maxDistance = -1)
    {
        if (pattern.Length == 0 || text.Length == 0) return false;
        if (maxDistance < 0) maxDistance = pattern.Length <= 6 ? 1 : 2;
        if (maxDistance == 0 || text.Contains(pattern, StringComparison.Ordinal)) return true;

        var m = pattern.Length;
        var n = text.Length;
        var previous = new int[n + 1];
        var current = new int[n + 1];
        // previous[j] = distance(pattern prefix, text prefix j); row 0 is all
        // zeros because the pattern may start anywhere in the text.
        for (var i = 1; i <= m; i++)
        {
            current[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var substitution = previous[j - 1] + (pattern[i - 1] == text[j - 1] ? 0 : 1);
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;
                var best = substitution <= deletion ? substitution : deletion;
                if (insertion < best) best = insertion;
                current[j] = best;
            }
            (previous, current) = (current, previous);
        }
        for (var j = 0; j <= n; j++)
        {
            if (previous[j] <= maxDistance) return true;
        }
        return false;
    }
}
