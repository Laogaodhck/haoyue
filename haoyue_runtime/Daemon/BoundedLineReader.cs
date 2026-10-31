using System.Text;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// 单行读取结果：<see cref="Line"/> 为 null 时，<see cref="TooLong"/> 表示该行超过调用方给的上限，
/// 否则表示流已结束。
/// </summary>
internal readonly record struct BoundedLineRead(string? Line, bool TooLong);

/// <summary>
/// 有长度上限的逐行读取器。<see cref="StreamReader.ReadLineAsync(CancellationToken)"/> 会把整行
/// 读进内存且没有上限——一条不含换行的巨行就能把 daemon 内存吃满。这里在读取过程中截停：
/// 一旦超过调用方给的上限立即返回 TooLong，由调用方回错误并断开连接（不再尝试对齐下一个换行）。
/// 同一连接上只能有一个实例：内部缓冲会预读，多个实例共享底层流会丢字符。
/// </summary>
internal sealed class BoundedLineReader(TextReader reader)
{
    private readonly char[] _buffer = new char[4096];
    private readonly StringBuilder _line = new();
    private int _offset;
    private int _count;

    /// <summary>读取下一行（不含换行符，CRLF 的 CR 一并去掉）。</summary>
    public async ValueTask<BoundedLineRead> ReadLineAsync(int maxLineLength, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineLength, 1);
        _line.Clear();

        while (true)
        {
            if (_offset == _count)
            {
                _count = await reader.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0)
                    return _line.Length == 0 ? new(null, false) : new(_line.ToString(), false);
            }

            while (_offset < _count)
            {
                var ch = _buffer[_offset++];
                if (ch == '\n')
                {
                    var text = _line.ToString();
                    return new(text.EndsWith('\r') ? text[..^1] : text, false);
                }

                if (_line.Length >= maxLineLength) return new(null, true);
                _line.Append(ch);
            }
        }
    }
}
