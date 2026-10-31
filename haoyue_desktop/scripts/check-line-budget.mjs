import { readdir, readFile } from 'node:fs/promises'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * 组件体积守门：单文件组件超过 2000 行说明它承担了多个正交职责，
 * 应在合并前拆出面板/组合式函数，而不是继续往上堆。
 */
const LINE_BUDGET = 2000
const ROOT = fileURLToPath(new URL('..', import.meta.url))
const TARGETS = ['src/renderer/src', 'src/main']

async function collect(directory, extension, found = []) {
  let entries
  try {
    entries = await readdir(directory, { withFileTypes: true })
  } catch {
    return found
  }
  for (const entry of entries) {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) await collect(path, extension, found)
    else if (entry.name.endsWith(extension)) found.push(path)
  }
  return found
}

const offenders = []
const measured = []
for (const target of TARGETS) {
  for (const file of await collect(join(ROOT, target), '.vue')) {
    const lines = (await readFile(file, 'utf8')).split('\n').length
    measured.push([relative(ROOT, file), lines])
    if (lines > LINE_BUDGET) offenders.push([relative(ROOT, file), lines])
  }
}

measured.sort((left, right) => right[1] - left[1])
console.log(`Largest components (budget ${LINE_BUDGET} lines):`)
for (const [file, lines] of measured.slice(0, 5)) console.log(`  ${lines.toString().padStart(5)}  ${file}`)

if (offenders.length > 0) {
  console.error(`\n${offenders.length} component(s) exceed the ${LINE_BUDGET}-line budget:`)
  for (const [file, lines] of offenders) console.error(`  ${lines}  ${file}`)
  process.exitCode = 1
}
