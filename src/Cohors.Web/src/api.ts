import { PublicClientApplication } from '@azure/msal-browser'
import type { Session } from './types'

let csrf = ''
let accessToken: string | null = null
let msal: PublicClientApplication | null = null
export class ApiError extends Error {
  constructor(message: string, public readonly status: number, public readonly code: string) { super(message) }
}
async function request(path: string, data?: unknown): Promise<Response> {
  const headers: Record<string, string> = {}
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`
  if (data !== undefined) {
    headers['Content-Type'] = 'application/json'
    headers['X-CSRF-Token'] = csrf
    headers['Idempotency-Key'] = crypto.randomUUID()
  }
  const response = await fetch(`/api${path}`, { method: data === undefined ? 'GET' : 'POST', headers, credentials: 'same-origin', body: data === undefined ? undefined : JSON.stringify(data) })
  if (!response.ok) {
    const error = await response.json().catch(() => ({ title: `HTTP ${response.status}: Operation nicht bestaetigt.`, code: 'http_error' }))
    throw new ApiError(error.title ?? error.message ?? `HTTP ${response.status}`, response.status, error.code ?? 'http_error')
  }
  return response
}
export async function api<T>(path: string, data?: unknown): Promise<T> {
  const response = await request(path, data)
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}
export async function downloadApi(path: string, filename: string): Promise<void> {
  const response = await request(path)
  const url = URL.createObjectURL(await response.blob())
  try {
    const anchor = document.createElement('a')
    anchor.href = url; anchor.download = filename; document.body.appendChild(anchor); anchor.click(); anchor.remove()
  } finally { window.setTimeout(() => URL.revokeObjectURL(url), 1000) }
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
