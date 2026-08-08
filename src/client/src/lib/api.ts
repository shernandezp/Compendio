/**
 * The API client.
 *
 * Cookie authentication with `SameSite=Strict` and no CORS, so there is no token to attach and no
 * anti-forgery header to remember — `credentials: 'same-origin'` is the whole story.
 */

/** A `ProblemDetails` from the server, with its stable machine code. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    readonly title: string,
    readonly detail: string,
    readonly extensions: Record<string, unknown> = {},
  ) {
    super(`${code}: ${title}`);
    this.name = 'ApiError';
  }

  get isConflict(): boolean {
    return this.code === 'page.conflict';
  }

  get isNotFound(): boolean {
    return this.status === 404;
  }

  get isUnauthenticated(): boolean {
    return this.status === 401;
  }

  /** Field errors from a validation failure, if any. */
  get fieldErrors(): Record<string, string[]> {
    return (this.extensions.errors as Record<string, string[]>) ?? {};
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(path, {
    ...init,
    credentials: 'same-origin',
    headers: {
      Accept: 'application/json',
      ...(init.body !== undefined && !(init.body instanceof FormData)
        ? { 'Content-Type': 'application/json' }
        : {}),
      ...init.headers,
    },
  });

  if (response.status === 204) {
    return undefined as T;
  }

  const isJson = response.headers.get('content-type')?.includes('json') ?? false;
  const body = isJson ? await response.json() : await response.text();

  if (!response.ok) {
    if (isJson && body && typeof body === 'object') {
      const { title, detail, code, status, instance, type, ...extensions } = body as Record<string, unknown>;
      void status;
      void instance;
      void type;

      throw new ApiError(
        response.status,
        (code as string) ?? 'error.unexpected',
        (title as string) ?? '',
        (detail as string) ?? '',
        extensions,
      );
    }

    throw new ApiError(response.status, 'error.unexpected', '', String(body));
  }

  return body as T;
}

/**
 * Percent-encodes a content path for a URL, segment by segment.
 *
 * The separators stay separators — the API's catch-all routes want them — but everything else is
 * encoded. `PathPolicy` allows characters a URL does not read the way a file name means them: `C#`
 * is a perfectly good page name in an IT wiki, and unencoded it truncates the request at the `#`
 * and asks for a page called `C`.
 */
export const encodePath = (path: string) => path.split('/').map(encodeURIComponent).join('/');

const get = <T>(path: string) => request<T>(path);

/**
 * `signal` is only threaded through the calls that can take minute-scale time — the AI ones.
 * Everywhere else a request is over before a cancel button could be found, and an abortable
 * signature on all forty would be forty places to pass undefined.
 */
const post = <T>(path: string, body?: unknown, signal?: AbortSignal) =>
  request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body), signal });
const put = <T>(path: string, body: unknown) =>
  request<T>(path, { method: 'PUT', body: JSON.stringify(body) });
const del = <T>(path: string) => request<T>(path, { method: 'DELETE' });

/**
 * Whether a rejection is "the caller cancelled", which is not a failure to report.
 *
 * Matched on `name` rather than on `instanceof DOMException`: browsers throw a `DOMException` here,
 * but jsdom and Node's undici throw a plain `Error` with the same name, and a check that is true in
 * the browser and false under test is the worst of both — it would render "AbortError" at a user who
 * pressed Cancel, and no test would catch it.
 */
export const isAbort = (error: unknown) =>
  error instanceof Error && (error.name === 'AbortError' || error.name === 'TimeoutError');

// ---- Shapes the API returns. Regenerated into schema.d.ts by openapi-typescript in CI; these are
// ---- the hand-written subset the screens actually use.

export type PermissionLevel = 'None' | 'Read' | 'Write' | 'Manage';
export type UserRole = 'Reader' | 'Editor' | 'Admin';

export interface Page {
  path: string;
  title: string;
  lang?: string;
  translationKey?: string;
  tags: string[];
  owner?: string;
  reviewIntervalDays?: number;
  nextReviewDate?: string;
  requiresAcknowledgment: boolean;
  /** Computed by the server, so the banner and the stale report cannot disagree. */
  isStale: boolean;
  contentHash: string;
  byteSize: number;
  updatedAt: string;
  updatedBy?: string;
  lastEditWasExternal: boolean;
  isSecure: boolean;
  isCanonical: boolean;
  level: PermissionLevel;
  content?: string;
  html?: string;
  headings: { level: number; text: string; anchor: string }[];
  containsMermaid: boolean;
  translations: { path: string; lang: string; title: string; isStale: boolean }[];
  attachments: { path: string; name: string; contentType: string; byteSize: number; createdAt: string }[];
}

export interface TreeNode {
  path: string;
  name: string;
  title: string;
  isFolder: boolean;
  isSecure: boolean;
  level: PermissionLevel;
  lang?: string;
  children: TreeNode[];
}

/** The navigation tree plus the caller's effective level at the root. */
export interface Tree {
  rootLevel: PermissionLevel;
  nodes: TreeNode[];
}

export interface SearchHit {
  path: string;
  title: string;
  excerpt: string;
  lang?: string;
  tags: string[];
  updatedAt: string;
}

export interface Paged<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface User {
  id: string;
  userName: string;
  displayName: string;
  email?: string;
  role: UserRole;
  active: boolean;
  preferredLanguage?: string;
  createdAt: string;
  lastSignInAt?: string;
  groupIds: string[];
}

export interface Session {
  authenticated: boolean;
  needsSetup: boolean;
  language: string;
  user?: User;
}

export interface Group {
  id: string;
  name: string;
  active: boolean;
  memberIds: string[];
}

export interface BackupResult {
  path: string;
  fileName: string;
  files: number;
  secureScopes: number;
  keyWrapped: boolean;
  takenAt: string;
}

export interface SetupState {
  needsSetup: boolean;
  defaultLanguage: string;
  languages: { code: string; englishName: string; nativeName: string }[];
  contentRoot: string;
}

export interface VersionSummary {
  id: string;
  sequence: number;
  createdAt: string;
  authorUserId?: string;
  authorDisplayName?: string;
  source: 'Editor' | 'External' | 'Move' | 'Delete' | 'Restore' | 'Normalization';
  contentHash: string;
  byteSize: number;
  note?: string;
  path: string;
}

export interface PageDiff {
  from: VersionSummary;
  to: VersionSummary;
  source: { kind: string; rightLine?: number; text: string; pieces: { kind: string; text: string }[] }[];
  renderedHtml: string;
  addedLines: number;
  removedLines: number;
}

export interface Acl {
  folderPath: string;
  inheritParent: boolean;
  entries: AclEntry[];
  inheritedFrom: AclEntry[];
  isSecure: boolean;
  updatedAt?: string;
}

export interface AclEntry {
  subjectType: 'User' | 'Group' | 'Everyone';
  subjectId?: string;
  subjectName: string;
  level: PermissionLevel;
}

export interface SecureScope {
  folderPath: string;
  keyId: string;
  createdAt: string;
  rotatedAt?: string;
  indexContent: boolean;
  allowAi: boolean;
  /** "Available" | "MasterKeyMissing" | "MasterKeyUnreadable" | "DataKeyUnwrappable" */
  availability: string;
  encryptionCount: number;
}

export interface AuditEntry {
  id: string;
  at: string;
  actorUserId?: string;
  actorDisplayName?: string;
  action: string;
  targetType: string;
  targetPath: string;
}

export interface Status {
  version: string;
  installMode: string;
  contentRoot: string;
  pageCount: number;
  folderCount: number;
  watcherMode: string;
  indexState: string;
  indexQueueDepth: number;
  secureAvailability: string;
  databaseBytes: number;
  contentBytes: number;
  lastBackupAt?: string;
}

export interface PageLifecycle {
  path: string;
  title: string;
  owner?: string;
  /** Absent when the owner string matches no active account. Reported, never rewritten. */
  ownerUserId?: string;
  ownerDisplayName?: string;
  reviewIntervalDays?: number;
  nextReviewDate?: string;
  requiresAcknowledgment: boolean;
  isStale: boolean;
}

export interface StalePage {
  path: string;
  title: string;
  owner?: string;
  ownerDisplayName?: string;
  unassigned: boolean;
  nextReviewDate?: string;
  daysOverdue?: number;
  updatedAt: string;
}

export type NotificationKind =
  | 'PageStale'
  | 'OwnedPageEditedExternally'
  | 'TranslationSourceChanged'
  | 'AcknowledgmentRequested'
  | 'AcknowledgmentOverdue'
  | 'GitMirrorFailed';

export interface Notification {
  id: string;
  kind: NotificationKind;
  targetPath: string;
  payloadJson?: string;
  createdAt: string;
  readAt?: string;
}

export interface AcknowledgmentTask {
  path: string;
  title: string;
  sinceVersionAt: string;
  overdue: boolean;
}

export interface Dashboard {
  myStalePages: StalePage[];
  myPageCount: number;
  unreadNotificationCount: number;
  recentNotifications: Notification[];
  outstandingAcknowledgments: AcknowledgmentTask[];
}

export interface AcknowledgmentReport {
  path: string;
  title: string;
  currentVersionId: string;
  currentVersionSequence: number;
  requiredCount: number;
  acknowledgedCount: number;
  people: {
    userId: string;
    displayName: string;
    hasAcknowledged: boolean;
    acknowledgedVersionId?: string;
    acknowledgedAt?: string;
  }[];
}

/**
 * What the client renders AI from.
 *
 * `enabled: false` means no AI control appears anywhere — not disabled, not greyed out, absent.
 * Every AI action returns 404 in that state, so a rendered button would be a button that fails.
 */
export interface AiStatus {
  enabled: boolean;
  features: string[];
  /** The host content is sent to. Shown next to every AI action, never hidden in settings. */
  endpointLabel: string;
  model: string;
  budget: AiBudget;
}

/**
 * What is left of the caller's daily AI allowance.
 *
 * A rolling 24 hours, not a calendar day — so "8 of 50 used in the last 24 hours" is exactly what the
 * server enforces rather than a rounding of it.
 */
export interface AiBudget {
  /** 0 when no cap is set, and then `remaining` is null. */
  limit: number;
  used: number;
  /** Null means unlimited. Shown as nothing at all, never as a made-up number. */
  remaining: number | null;
  /** When the oldest counted request ages out — the moment one more becomes possible. */
  resetsAt: string | null;
}

export interface AiProposal {
  proposal: string;
  model: string;
  endpointLabel: string;
}

export interface AiAnswer {
  answer: string;
  citations: { path: string; title: string }[];
  model: string;
  endpointLabel: string;
}

export interface AiSettings {
  enabled: boolean;
  baseUrl: string;
  model: string;
  hasApiKey: boolean;
  endpointLabel: string;
  allowedSpaces: string[];
  disabledFeatures: string[];
  availableFeatures: string[];
  /** Requests one person may make in a rolling 24 hours. 0 means no limit. */
  dailyPerUser: number;
  /** Requests everybody together may make. 0 means no limit. */
  dailyPerInstance: number;
  /** What the instance has actually spent, so a cap is set against a real number. */
  instanceUsage: AiBudget;
  topSpenders: { displayName: string; requests: number }[];
}

export interface GitMirrorStatus {
  enabled: boolean;
  gitAvailable: boolean;
  remoteConfigured: boolean;
  branch: string;
  intervalMinutes: number;
  lastSuccessAt?: string;
  lastAttemptAt?: string;
  lastCommit?: string;
  lastError?: string;
  consecutiveFailures: number;
}

/** Every call the SPA makes, in one place. */
export const api = {
  session: () => get<Session>('/api/v1/auth/me'),
  login: (userName: string, password: string, persistent: boolean) =>
    post<User>('/api/v1/auth/login', { userName, password, persistent }),
  logout: () => post<void>('/api/v1/auth/logout'),
  updateProfile: (body: { displayName?: string; email?: string; preferredLanguage?: string }) =>
    put<User>('/api/v1/auth/profile', body),
  changePassword: (currentPassword: string, newPassword: string) =>
    post<void>('/api/v1/auth/password', { currentPassword, newPassword }),

  setupState: () => get<SetupState>('/api/v1/setup/state'),
  completeSetup: (body: Record<string, unknown>) => post<User>('/api/v1/setup', body),

  tree: () => get<Tree>('/api/v1/tree'),

  page: (path: string, raw = false) => get<Page>(`/api/v1/pages/${encodePath(path)}${raw ? '?raw=true' : ''}`),
  createPage: (body: { folderPath: string; title: string; content?: string; lang?: string }) =>
    post<Page>('/api/v1/pages', body),
  /**
   * @param materialRevision The editor's explicit "everyone must read this again". Default off, so
   * an ordinary save never re-opens an acknowledgment.
   */
  savePage: (path: string, content: string, expectedHash: string, normalized = false, materialRevision = false) =>
    put<Page>(`/api/v1/pages/${encodePath(path)}`, { content, expectedHash, normalized, materialRevision }),
  deletePage: (path: string) => del<void>(`/api/v1/pages/${encodePath(path)}`),
  movePage: (path: string, targetPath: string) => post<Page>('/api/v1/pages/move', { path, targetPath }),
  /** Changes only the front-matter title. The file name and path are unchanged, so the URL survives. */
  setPageTitle: (path: string, title: string) => post<Page>('/api/v1/pages/title', { path, title }),
  toggleCheckbox: (path: string, offset: number, checked: boolean, expectedHash: string) =>
    post<Page>('/api/v1/pages/checkbox', { path, offset, checked, expectedHash }),
  backlinks: (path: string) => get<SearchHit[]>(`/api/v1/pages/backlinks?path=${encodeURIComponent(path)}`),

  createFolder: (parentPath: string, name: string) => post<TreeNode>('/api/v1/folders', { parentPath, name }),
  deleteFolder: (path: string) => del<void>(`/api/v1/folders/${encodePath(path)}`),
  /** Returns no content: the caller refetches the tree, which is the only thing a move changes. */
  moveFolder: (path: string, targetPath: string) => post<void>('/api/v1/folders/move', { path, targetPath }),

  search: (query: string, page = 1, pageSize = 20) =>
    get<Paged<SearchHit>>(`/api/v1/search?q=${encodeURIComponent(query)}&page=${page}&pageSize=${pageSize}`),
  suggest: (query: string) => get<SearchHit[]>(`/api/v1/search/suggest?q=${encodeURIComponent(query)}`),
  tags: () => get<{ tag: string; count: number }[]>('/api/v1/tags'),
  recent: (limit = 10) => get<SearchHit[]>(`/api/v1/recent?limit=${limit}`),

  versions: (path: string) => get<VersionSummary[]>(`/api/v1/versions?path=${encodeURIComponent(path)}`),
  versionContent: (id: string) => get<{ id: string; path: string; content: string }>(`/api/v1/versions/${id}`),
  diff: (path: string, from: string, to: string) =>
    get<PageDiff>(`/api/v1/diff?path=${encodeURIComponent(path)}&from=${from}&to=${to}`),
  restore: (id: string, path: string) => post<Page>(`/api/v1/versions/${id}/restore`, { path }),

  acl: (path: string) => get<Acl>(`/api/v1/acl/${encodePath(path)}`),
  setAcl: (path: string, inheritParent: boolean, entries: unknown[]) =>
    put<Acl>(`/api/v1/acl/${encodePath(path)}`, { inheritParent, entries }),
  effectiveAccess: (path: string, userId: string) =>
    get<{ userId: string; displayName: string; level: PermissionLevel; reason: string }>(
      `/api/v1/acl/effective?path=${encodeURIComponent(path)}&userId=${userId}`,
    ),

  secureScopes: () => get<SecureScope[]>('/api/v1/admin/secure-scopes'),
  createSecureScope: (path: string, indexContent: boolean, allowAi: boolean) =>
    post<SecureScope>('/api/v1/admin/secure-scopes', { path, indexContent, allowAi }),
  updateSecureScope: (path: string, body: { indexContent?: boolean; allowAi?: boolean }) =>
    put<void>(`/api/v1/admin/secure-scopes/${encodePath(path)}`, body),

  auditLog: (page = 1, pageSize = 50) =>
    get<Paged<AuditEntry>>(`/api/v1/admin/audit?page=${page}&pageSize=${pageSize}`),

  users: () => get<User[]>('/api/v1/admin/users'),
  createUser: (body: Record<string, unknown>) => post<User>('/api/v1/admin/users', body),
  updateUser: (id: string, body: Record<string, unknown>) => put<User>(`/api/v1/admin/users/${id}`, body),
  setUserPassword: (id: string, newPassword: string) =>
    post<void>(`/api/v1/admin/users/${id}/password`, { newPassword }),
  groups: () => get<Group[]>('/api/v1/admin/groups'),
  createGroup: (name: string) => post<Group>('/api/v1/admin/groups', { name }),
  updateGroup: (id: string, body: { name?: string; active?: boolean; memberIds?: string[] }) =>
    put<Group>(`/api/v1/admin/groups/${id}`, body),
  status: () => get<Status>('/api/v1/admin/status'),
  reindex: () => post<void>('/api/v1/admin/reindex'),
  reconcile: () => post<void>('/api/v1/admin/reconcile'),
  createBackup: (passphrase?: string) =>
    post<BackupResult>('/api/v1/admin/backup', passphrase ? { passphrase } : {}),

  languages: () => get<{ code: string; englishName: string; nativeName: string }[]>('/api/v1/languages'),
  about: () => get<{ product: string; version: string; license: string; sourceUrl: string; licenseNotice: string; instanceName: string }>('/api/v1/about'),

  // ---- Lifecycle -------------------------------------------------------------------------------
  setLifecycle: (body: {
    path: string;
    owner?: string | null;
    reviewIntervalDays?: number | null;
    nextReviewDate?: string | null;
    requiresAcknowledgment?: boolean | null;
  }) => put<PageLifecycle>('/api/v1/pages/lifecycle', body),
  confirmReviewed: (path: string) => post<PageLifecycle>('/api/v1/pages/review-confirm', { path }),
  staleReport: (page = 1, pageSize = 50, owner?: string, space?: string) =>
    get<Paged<StalePage>>(
      `/api/v1/lifecycle/stale?page=${page}&pageSize=${pageSize}` +
        (owner ? `&owner=${encodeURIComponent(owner)}` : '') +
        (space ? `&space=${encodeURIComponent(space)}` : ''),
    ),
  dashboard: () => get<Dashboard>('/api/v1/dashboard'),
  /** Active accounts for the owner picker. Not the admin list — three fields and no more. */
  pickableUsers: () => get<{ id: string; userName: string; displayName: string }[]>('/api/v1/users'),

  // ---- Notifications ---------------------------------------------------------------------------
  notifications: (page = 1, pageSize = 25, unreadOnly = false) =>
    get<Paged<Notification>>(`/api/v1/notifications?page=${page}&pageSize=${pageSize}&unreadOnly=${unreadOnly}`),
  notificationCount: () => get<{ count: number }>('/api/v1/notifications/count'),
  markNotificationRead: (id: string) => post<void>(`/api/v1/notifications/${id}/read`),
  markAllNotificationsRead: () => post<void>('/api/v1/notifications/read-all'),

  // ---- Acknowledgments -------------------------------------------------------------------------
  acknowledge: (path: string) =>
    post<{ path: string; pageVersionId: string; acknowledgedAt: string }>('/api/v1/acknowledgments', { path }),
  acknowledgmentReport: (path: string) =>
    get<AcknowledgmentReport>(`/api/v1/acknowledgments/page?path=${encodeURIComponent(path)}`),
  myAcknowledgments: () => get<AcknowledgmentTask[]>('/api/v1/acknowledgments/mine'),

  // ---- AI. Every action 404s when no provider is configured, which is why the UI reads status. --
  // Every action also takes a signal: a local model on CPU can take the full two-minute timeout, and
  // a progress spinner with no way out is the difference between "slow" and "stuck".
  aiStatus: () => get<AiStatus>('/api/v1/ai/status'),
  /**
   * The page-template catalogue — bundled entries plus any `_templates/` overrides.
   *
   * `title` is an i18n key for the bundled ones and a literal for an organization's own, which is
   * why the caller translates with the title as its own fallback.
   */
  templates: () => get<{ id: string; title: string; description?: string; content: string }[]>('/api/v1/templates'),
  aiImprove: (path: string, text?: string, signal?: AbortSignal) =>
    post<AiProposal>('/api/v1/ai/improve', { path, text }, signal),
  aiSummarize: (path: string, text?: string, signal?: AbortSignal) =>
    post<AiProposal>('/api/v1/ai/summarize', { path, text }, signal),
  aiFreshness: (path: string, signal?: AbortSignal) =>
    post<AiProposal>('/api/v1/ai/freshness', { path }, signal),
  aiDraft: (folderPath: string, bullets: string, templateId?: string, signal?: AbortSignal) =>
    post<AiProposal>('/api/v1/ai/draft', { folderPath, bullets, templateId }, signal),
  aiTranslate: (path: string, targetLanguage: string, signal?: AbortSignal) =>
    post<Page>('/api/v1/ai/translate', { path, targetLanguage }, signal),
  aiAsk: (question: string, signal?: AbortSignal) =>
    post<AiAnswer>('/api/v1/ai/ask', { question }, signal),

  aiSettings: () => get<AiSettings>('/api/v1/admin/ai'),
  /** Every field is optional, and an omitted one is left as it was — including the key. */
  saveAiSettings: (body: {
    baseUrl?: string;
    model?: string;
    /** Omit to leave the stored key alone; send an empty string to clear it. */
    apiKey?: string;
    allowedSpaces?: string[];
    disabledFeatures?: string[];
    dailyPerUser?: number;
    dailyPerInstance?: number;
  }) => put<AiSettings>('/api/v1/admin/ai', body),
  clearAiSettings: () => del<AiSettings>('/api/v1/admin/ai'),
  testAiConnection: () => post<{ ok: boolean; detail: string; model?: string }>('/api/v1/admin/ai/test'),

  // ---- Git mirror ------------------------------------------------------------------------------
  gitMirror: () => get<GitMirrorStatus>('/api/v1/admin/git-mirror'),
  pushGitMirror: () => post<GitMirrorStatus>('/api/v1/admin/git-mirror/push'),

  uploadAttachment: (pagePath: string, file: File) => {
    const form = new FormData();
    form.append('pagePath', pagePath);
    form.append('file', file);
    return request<{ path: string; name: string }>('/api/v1/attachments', { method: 'POST', body: form });
  },
};
