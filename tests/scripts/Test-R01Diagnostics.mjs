import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'

const requireWeb = createRequire(new URL('../../src/Cohors.Web/package.json', import.meta.url))
const ts = requireWeb('typescript')
const source = ts.createSourceFile('pilot.spec.ts',
  readFileSync(new URL('../../src/Cohors.Web/e2e/pilot.spec.ts', import.meta.url), 'utf8'),
  ts.ScriptTarget.Latest, true)
const functions = source.statements.filter(node => ts.isFunctionDeclaration(node) && node.name?.text === 'withR01Cleanup')
assert.equal(functions.length, 1, 'Expected one actual R01 cleanup implementation.')
const compiled = ts.transpileModule(functions[0].getText(source), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext },
}).outputText
const run = new Function(`${compiled}\nreturn withR01Cleanup;`)()
const primary = new Error('original UI assertion, not a cleanup failure')
const first = new Error('first owned context close failed')
const last = new Error('last owned API dispose failed')
const attempted = []
const actions = [
  { name: 'first browser', run: () => { attempted.push('first'); throw first } },
  { name: 'next browser', run: async () => { attempted.push('next') } },
  { name: 'last API', run: async () => { attempted.push('last'); throw last } },
]
await assert.rejects(run(async () => { throw primary }, actions), error => {
  assert(error instanceof AggregateError)
  assert.deepEqual(error.errors, [primary, first, last])
  assert.equal(error.cause, primary)
  for (const original of [primary, first, last]) assert(error.message.includes(original.stack))
  assert(error.message.includes('R01 cleanup: first browser'))
  assert(error.message.includes('R01 cleanup: last API'))
  return true
})
assert.deepEqual(attempted, ['first', 'next', 'last'])
await assert.rejects(run(async () => { throw primary }, []), error => error === primary)
await assert.rejects(run(async () => {}, [actions[2]]), error =>
  error instanceof AggregateError && error.errors.length === 1 && error.errors[0] === last)
const partial = []
let partialDisposed = false
await assert.rejects(run(async () => {
  partial.push({ name: 'acquired before setup failure', run: async () => { partialDisposed = true } })
  throw primary
}, partial), error => error === primary)
assert.equal(partialDisposed, true)
let successfulCleanup = false
await run(async () => {}, [{ name: 'owned context', run: async () => { successfulCleanup = true } }])
assert.equal(successfulCleanup, true)
let falsyThrown = false
try { await run(async () => { throw undefined }, []) } catch (error) { falsyThrown = true; assert.equal(error, undefined) }
assert.equal(falsyThrown, true)
console.log(JSON.stringify({
  result: 'Passed',
  cases: ['OriginalAndAllCleanupFailures', 'OriginalOnly', 'CleanupOnly', 'PartialSetup', 'SuccessfulCleanup', 'FalsyOriginal'],
  boundary: 'Actual helper with synthetic callbacks only; no SQL, API, browser or test account opened.',
}, null, 2))
