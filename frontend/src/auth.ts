export type AuthSession = {
  accessToken: string;
  expiresAt: string;
  user: { id: string; email: string; username: string; displayName: string | null; profileImageUrl: string | null; profileImageVersion: string | null };
};

export type AccountStatus = {
  id: string;
  email: string;
  hasPassword: boolean;
  externalLogins: string[];
};

export class AuthApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly details: string[];

  constructor(code: string, status = 0, details: string[] = []) {
    const normalizedCode = code === "Invalid CSRF token." ? "invalid_csrf" : code;
    super(normalizedCode);
    this.code = normalizedCode;
    this.status = status;
    this.details = details;
  }
}

let session: AuthSession | null = null;
let csrfToken: string | null = null;
let csrfInFlight: Promise<string> | null = null;
let bootstrapInFlight: Promise<AuthSession | null> | null = null;
let refreshInFlight: Promise<AuthSession | null> | null = null;
let externalExchangeInFlight: Promise<AuthSession> | null = null;
let sessionExpiredHandler: (() => void) | null = null;

export function setSessionExpiredHandler(handler: (() => void) | null): void {
  sessionExpiredHandler = handler;
}

async function errorFromResponse(response: Response): Promise<AuthApiError> {
  const body: { error?: string; code?: string; errors?: Record<string, string[]> | string[] } =
    await response.json().catch(() => ({}));
  const details = Array.isArray(body.errors)
    ? body.errors
    : Object.values(body.errors ?? {}).flat();
  return new AuthApiError(
    body.code ?? body.error ??
      (response.status === 401 ? "unauthenticated" : "authentication_failed"),
    response.status,
    details,
  );
}

async function fetchCsrfToken(accessToken?: string): Promise<string> {
  const response = await fetch("/api/auth/csrf", {
    credentials: "include",
    cache: "no-store",
    headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : undefined,
  });
  if (!response.ok) throw await errorFromResponse(response);
  const body: { token?: string } = await response.json();
  if (typeof body?.token !== "string" || !body.token.trim())
    throw new AuthApiError("csrf_unavailable", 503);
  return body.token;
}

async function getCsrfToken(fresh = false, rejectedToken?: string): Promise<string> {
  // Concurrent rejected requests share the replacement instead of invalidating it again.
  if (fresh && (rejectedToken === undefined || csrfToken === rejectedToken)) csrfToken = null;
  if (csrfToken) return csrfToken;
  if (csrfInFlight) return csrfInFlight;
  csrfInFlight = fetchCsrfToken();
  try {
    csrfToken = await csrfInFlight;
    return csrfToken;
  } finally {
    csrfInFlight = null;
  }
}

async function isCsrfRejection(response: Response): Promise<boolean> {
  if (response.status !== 400 && response.status !== 403) return false;
  const body = await response.clone().json().catch(() => ({}));
  return body?.code === "invalid_csrf" || body?.error === "Invalid CSRF token.";
}

/** Cookie-auth endpoints validate antiforgery before changing or rotating a session. */
async function csrfFetch(path: string, init: RequestInit): Promise<Response> {
  const token = await getCsrfToken();
  const send = (value: string) => {
    const headers = new Headers(init.headers);
    headers.set("X-CSRF-TOKEN", value);
    return fetch(path, { ...init, headers, credentials: "include", cache: "no-store" });
  };
  const response = await send(token);
  if (!await isCsrfRejection(response)) return response;
  return send(await getCsrfToken(true, token)); // Exactly one retry, never a network-error retry.
}

/** Every app startup reacquires CSRF; the HttpOnly cookie remains the durable login credential. */
export async function bootstrapSession(): Promise<AuthSession | null> {
  if (bootstrapInFlight) return bootstrapInFlight;
  bootstrapInFlight = (async () => {
    await getCsrfToken(true);
    return restoreSession();
  })();
  try { return await bootstrapInFlight; }
  finally { bootstrapInFlight = null; }
}

async function startSession(
  path: string,
  email: string,
  password: string,
): Promise<AuthSession> {
  const response = await csrfFetch(path, {
    method: "POST",
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ email, password }),
  });
  if (!response.ok) {
    if (path.endsWith("/login") && response.status === 401)
      throw new AuthApiError("invalid_credentials", 401);
    throw await errorFromResponse(response);
  }

  session = await response.json();
  return session!;
}

export function register(
  email: string,
  password: string,
): Promise<AuthSession> {
  return startSession("/api/auth/register", email, password);
}

export function login(email: string, password: string): Promise<AuthSession> {
  return startSession("/api/auth/login", email, password);
}

export function startGoogleLogin(): void {
  window.location.assign("/api/auth/external/google");
}

export function exchangeGoogleCode(code: string): Promise<AuthSession> {
  // React Strict Mode may run a callback effect twice in development. Both callers must
  // share one request because the database grant is deliberately single-use.
  externalExchangeInFlight ??= (async () => {
    await getCsrfToken(true);
    const response = await fetch("/api/auth/external/exchange", {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ code }),
    });
    if (!response.ok) throw await errorFromResponse(response);

    session = await response.json();
    return session!;
  })();
  return externalExchangeInFlight;
}

export async function startGoogleLink(): Promise<void> {
  const response = await apiFetch("/api/auth/external/google/link-intent", {
    method: "POST",
  });
  if (!response.ok) throw await errorFromResponse(response);
  // The backend derives the user from our JWT before browser navigation starts.
  window.location.assign("/api/auth/external/google/link");
}

export async function unlinkGoogle(): Promise<void> {
  const response = await apiFetch("/api/auth/external/google/link", {
    method: "DELETE",
  });
  if (!response.ok) throw await errorFromResponse(response);
}

export async function getAccountStatus(): Promise<AccountStatus> {
  const response = await apiFetch("/api/auth/account");
  if (!response.ok) throw await errorFromResponse(response);
  return response.json() as Promise<AccountStatus>;
}

export async function restoreSession(): Promise<AuthSession | null> {
  if (session && Date.parse(session.expiresAt) > Date.now() + 5000)
    return session;
  if (refreshInFlight) return refreshInFlight;

  refreshInFlight = (async () => {
    const response = await csrfFetch("/api/auth/refresh", {
      method: "POST",
      credentials: "include",
    });
    if (response.status === 401) {
      session = null;
      return null;
    }
    if (!response.ok) throw await errorFromResponse(response);

    session = await response.json();
    return session;
  })();

  try {
    return await refreshInFlight;
  } finally {
    refreshInFlight = null;
  }
}

export async function apiFetch(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<Response> {
  const request = new Request(input, init);
  const url = new URL(request.url);
  if (
    url.origin !== window.location.origin ||
    !url.pathname.startsWith("/api/")
  ) {
    throw new Error("Authenticated requests must target this app’s API.");
  }
  const active = await restoreSession();
  if (!active) {
    sessionExpiredHandler?.();
    throw new AuthApiError("unauthenticated", 401);
  }

  let csrfRetried = false;
  const send = async (accessToken: string) => {
    const headers = new Headers(request.headers);
    headers.set("Authorization", `Bearer ${accessToken}`);
    const fetchRequest = () => fetch(new Request(request.clone(), { headers, credentials: "include" }));
    const response = await fetchRequest();
    if (csrfRetried || !await isCsrfRejection(response)) return response;
    csrfRetried = true;
    // A bearer-protected endpoint needs a token bound to that same identity.
    // Keep it separate from the anonymous CSRF token used by cookie-only refresh/logout.
    headers.set("X-CSRF-TOKEN", await fetchCsrfToken(accessToken));
    return fetchRequest();
  };

  let response = await send(active.accessToken);
  if (response.status === 401) {
    session = null;
    const renewed = await restoreSession();
    if (renewed) response = await send(renewed.accessToken);
    else sessionExpiredHandler?.();
  }
  return response;
}

export function currentSession(): AuthSession | null {
  return session;
}

export function reconcileSessionUser(user: AuthSession["user"]): AuthSession | null {
  if (!session) return null;
  session = { ...session, user };
  return session;
}

/** Supplies a current short-lived bearer token to SignalR without exposing refresh credentials. */
export async function realtimeAccessToken(): Promise<string> {
  const activeSession = await restoreSession();
  if (!activeSession) {
    sessionExpiredHandler?.();
    throw new AuthApiError("unauthenticated", 401);
  }
  return activeSession.accessToken;
}

export async function logout(): Promise<void> {
  const response = await csrfFetch("/api/auth/logout", {
    method: "POST",
    credentials: "include",
  });
  if (!response.ok) throw await errorFromResponse(response);
  session = null;
}

export async function logoutEverywhere(): Promise<void> {
  const response = await apiFetch("/api/auth/logout-everywhere", {
    method: "POST",
  });
  if (!response.ok) throw new Error("Logout everywhere failed.");
  session = null;
}
