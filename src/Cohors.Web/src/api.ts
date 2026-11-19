import { PublicClientApplication } from '@azure/msal-browser'
import type { Session } from './types'

let csrf = ''
let accessToken: string | null = null
let msal: PublicClientApplication | null = null
export class ApiError extends Error {
  constructor(message: string, public readonly status: number, public readonly code: string) { super(message) }
}
async function request(path: string, data?: unknown, signal?: AbortSignal, idempotencyKey?: string): Promise<Response> {
  const headers: Record<string, string> = {}
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`
  if (data !== undefined) {
    headers['Content-Type'] = 'application/json'
    headers['X-CSRF-Token'] = csrf
    headers['Idempotency-Key'] = idempotencyKey ?? crypto.randomUUID()
  }
  const response = await fetch(`/api${path}`, { method: data === undefined ? 'GET' : 'POST', headers, credentials: 'same-origin', body: data === undefined ? undefined : JSON.stringify(data), signal })
  if (!response.ok) {
    const error = await response.json().catch(() => ({ title: `HTTP ${response.status}: Operation nicht bestaetigt.`, code: 'http_error' }))
    throw new ApiError(error.title ?? error.message ?? `HTTP ${response.status}`, response.status, error.code ?? 'http_error')
  }
  return response
}
export async function api<T>(path: string, data?: unknown, scope: { signal?: AbortSignal; idempotencyKey?: string } = {}): Promise<T> {
  const response = await request(path, data, scope.signal, scope.idempotencyKey)
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}
export async function downloadApi(path: string, filename: string, scope: { signal?: AbortSignal; isCurrent?: () => boolean } = {}): Promise<void> {
  function requireCurrent() {
    scope.signal?.throwIfAborted()
    if (scope.isCurrent && !scope.isCurrent()) throw new DOMException('Originalabruf nach Kontextwechsel abgebrochen.', 'AbortError')
  }
  requireCurrent()
  const response = await request(path, undefined, scope.signal)
  const bytes = await response.blob()
  requireCurrent()
  const url = URL.createObjectURL(bytes)
  const anchor = document.createElement('a')
  try {
    anchor.href = url; anchor.download = filename; document.body.appendChild(anchor)
    requireCurrent()
    anchor.click()
  } finally { anchor.remove(); window.setTimeout(() => URL.revokeObjectURL(url), 1000) }
}
export async function session(): Promise<Session> {
  const result = await api<Session>('/auth/session')
  csrf = result.csrfToken
  return result
}
export async function loginEntra(config: Session['entra']): Promise<Session> {
  msal = new PublicClientApplication({ auth: { clientId: config.clientId, authority: `https://login.microsoftonline.com/${config.tenantId}`, redirectUri: location.origin }, cache: { cacheLocation: 'sessionStorage' } })
  await msal.initialize()
  const result = await msal.loginPopup({ scopes: [config.scope] })
  accessToken = result.accessToken
  return session()
}
export async function logout(mode: string): Promise<void> {
  if (mode === 'Entra' && msal) { accessToken = null; await msal.logoutPopup(); return }
  await api('/auth/logout', {})
}
