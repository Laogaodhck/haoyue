/**
 * A path can never be a project when it is the user's profile directory or
 * Haoyue's own global state directory (~/.haoyue, or anything inside it).
 * Without this guard the desktop could register the profile itself as a project,
 * which combined with the runtime's workspace walk-up made every plain folder
 * under the profile resolve to the profile and share one session scope.
 */
export function isForbiddenProjectPath(path: string, homePath: string | undefined): boolean {
  if (!path || !homePath) return false

  const normalized = normalizePath(path)
  const home = normalizePath(homePath)
  const haoyueHome = normalizePath(`${home}/.haoyue`)
  if (!home || !haoyueHome) return false

  return (
    normalized === home ||
    normalized === haoyueHome ||
    normalized.startsWith(`${haoyueHome}/`)
  )
}

function normalizePath(path: string): string {
  return path.replace(/\\/g, '/').replace(/\/+$/, '').toLocaleLowerCase()
}
