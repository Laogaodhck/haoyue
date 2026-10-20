/// 时段问候（豆包式欢迎页）：按小时返回问候语。
/// 边界口径：5 点前算深夜，23 点后回到深夜；11-13 为正午，13-18 为下午。
export function dayGreeting(hour: number): string {
  if (!Number.isFinite(hour)) return '你好'
  if (hour < 5) return '夜深了'
  if (hour < 11) return '早上好'
  if (hour < 13) return '中午好'
  if (hour < 18) return '下午好'
  if (hour < 23) return '晚上好'
  return '夜深了'
}
