using Haoyue.Runtime.Data;

namespace Haoyue.Tests;

/// <summary>
/// 检索评测的共享数据集与打分器：固定语料 + 带标注查询集（基线关键词、无空格中文长问句、
/// 同义词、全角、多词组合、负例、错别字）。词法通道与混合通道共用同一份标注，
/// 两侧指标可直接对照，换算法不换题。
/// </summary>
internal static class KnowledgeEvalData
{
    // Dataset index +1 == the entry id assigned by sequential Save calls.
    public static readonly (string Title, string Content, string Tags)[] Dataset =
    [
        ("构建命令", "运行 python build.py --platform windows 打包，输出到 publish 目录", "build,打包"),
        ("数据库连接", "PostgreSQL 连接字符串在 appsettings.json 的 ConnectionStrings 节点配置，默认端口 5432", "数据库,配置"),
        ("部署流程", "使用 docker compose up -d 发布到服务器，默认端口 8080", "部署,docker,发布"),
        ("登录问题", "登录失败先检查账号密码，验证码 5 分钟内有效", "登录,账号"),
        ("版本管理", "version.py 统一管理版本号，改一处同步四个文件", "版本,version"),
        ("测试命令", "dotnet test 运行全部单元测试，pnpm test 运行前端测试", "测试,test"),
        ("包管理约定", "前端统一使用 pnpm，禁止 npm install，锁文件是 pnpm-lock.yaml", "pnpm,前端"),
        ("端口冲突", "daemon 默认端口 7860 被占用时用 --port 参数换一个", "端口,port"),
        ("日志位置", "运行日志在 %APPDATA%/haoyue/logs 目录下，按天滚动", "日志,log"),
        ("快捷键", "Ctrl+K 打开命令面板，Ctrl+N 新建会话，Esc 关闭弹窗", "快捷键,shortcut"),
        ("API 密钥", "模型 API key 配置在 settings 中，也支持环境变量 HAOYUE_API_KEY", "api,密钥,key"),
        ("中文编码", "Windows 控制台 GBK 乱码时执行 chcp 65001 切换 UTF-8", "编码,乱码"),
        ("全角符号", "代码里出现全角标点会编译失败，注意输入法状态", "全角,标点"),
        ("错别字对照", "「登陆」应为「登录」，「帐号」应为「账号」", "错别字"),
        ("性能优化", "大文件读取用流式处理，避免一次性载入内存", "性能,优化"),
        ("卸载步骤", "控制面板卸载后，手动删除 %APPDATA% 下的残留目录", "卸载,清理"),
    ];

    public static readonly (string Group, string Query, string[] ExpectedTitles)[] Scenarios =
    [
        // A: baseline single keywords — both algorithms must pass.
        ("A-基线", "打包", ["构建命令"]),
        ("A-基线", "python", ["构建命令"]),
        ("A-基线", "数据库", ["数据库连接"]),
        ("A-基线", "docker", ["部署流程"]),
        ("A-基线", "pnpm", ["包管理约定"]),
        // B: unspaced CJK questions — the legacy algorithm's main blind spot.
        ("B-CJK长查询", "连接字符串怎么配置", ["数据库连接"]),
        ("B-CJK长查询", "端口被占用怎么办", ["端口冲突"]),
        ("B-CJK长查询", "怎么打包发布", ["部署流程", "构建命令"]),
        ("B-CJK长查询", "日志文件在哪里", ["日志位置"]),
        ("B-CJK长查询", "大文件 内存", ["性能优化"]),
        // C: synonyms — "编译" finds "构建/打包" entries, typos like "登陆" find "登录".
        ("C-同义词", "编译", ["构建命令"]),
        ("C-同义词", "部署", ["部署流程"]),
        ("C-同义词", "上线流程", ["部署流程"]),
        ("C-同义词", "登入失败", ["登录问题"]),
        ("C-同义词", "帐号密码错误", ["登录问题", "错别字对照"]),
        ("C-同义词", "设置数据库", ["数据库连接"]),
        // D: full-width input and casing.
        ("D-全角大小写", "ＰＹＴＨＯＮ", ["构建命令"]),
        ("D-全角大小写", "DOTNET TEST", ["测试命令"]),
        ("D-全角大小写", "ｐｎｐｍ", ["包管理约定"]),
        // E: multi-token queries exercising coverage ranking.
        ("E-多词组合", "api key 环境变量", ["API 密钥"]),
        ("E-多词组合", "快捷键 命令面板", ["快捷键"]),
        // F: negatives — both algorithms must return nothing.
        ("F-负例", "不存在的词条", []),
        ("F-负例", "完全无关的查询词组", []),
        // G: typos handled by the edit-distance fallback on title/tags.
        ("G-错别字", "pinpm", ["包管理约定"]),
    ];

    /// <summary>负例查询（语料里确实没有答案），混合通道必须依旧返回空。</summary>
    public static IEnumerable<(string Group, string Query, string[] ExpectedTitles)> Negatives =>
        Scenarios.Where(s => s.ExpectedTitles.Length == 0);

    public sealed record Metrics(double Hit1, double Hit3, double Mrr, double NegativeRejection, int Missed)
    {
        public override string ToString() =>
            $"HitRate@1={Hit1:P1}  HitRate@3={Hit3:P1}  MRR={Mrr:P1}  负例拒绝率={NegativeRejection:P1}";
    }

    /// <summary>
    /// 按标注打分。正例：期望标题命中的名次决定 Hit@1/@3 与 1/rank；
    /// 负例：返回空才算命中（把最不离谱的条目当答案送回模型是失败，不是「兜底成功」）。
    /// </summary>
    public static Metrics Score(Func<string, int, IReadOnlyList<KnowledgeEntry>> search)
    {
        var hit1 = 0;
        var hit3 = 0;
        var negatives = 0;
        var negativesRejected = 0;
        var missed = 0;
        double reciprocalSum = 0;
        foreach (var (_, query, expected) in Scenarios)
        {
            var titles = search(query, 10).Select(e => e.Title).ToList();
            if (expected.Length == 0)
            {
                negatives++;
                if (titles.Count == 0)
                {
                    negativesRejected++;
                    hit1++;
                    hit3++;
                    reciprocalSum += 1;
                }
                else
                {
                    missed++;
                }
                continue;
            }

            var first = titles.FindIndex(expected.Contains) + 1;
            if (first >= 1)
            {
                if (first == 1) hit1++;
                if (first <= 3) hit3++;
                reciprocalSum += 1.0 / first;
            }
            else
            {
                missed++;
            }
        }
        var total = Scenarios.Length;
        return new Metrics(
            hit1 / (double)total, hit3 / (double)total, reciprocalSum / total,
            negatives == 0 ? 1 : negativesRejected / (double)negatives, missed);
    }

    /// <summary>逐题明细（评测报告用，便于失败时一眼看到是哪道题退化）。</summary>
    public static IReadOnlyList<string> PerQuery(string label, Func<string, int, IReadOnlyList<KnowledgeEntry>> search)
    {
        var lines = new List<string>();
        foreach (var (group, query, expected) in Scenarios)
        {
            var titles = search(query, 3).Select(e => e.Title).ToList();
            var hit = expected.Length == 0 ? titles.Count == 0 : expected.Any(titles.Contains);
            lines.Add($"[{label}][{group}] \"{query}\" → [{string.Join(", ", titles)}] {(hit ? "✓" : "✗ MISS")}");
        }
        return lines;
    }
}
