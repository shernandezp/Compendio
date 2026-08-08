import i18next from 'i18next';
import { initReactI18next } from 'react-i18next';
import en from './locales/en.json';
import es from './locales/es.json';

export const SUPPORTED_LANGUAGES = ['es', 'en'] as const;
export type Language = (typeof SUPPORTED_LANGUAGES)[number];

export const LANGUAGE_COOKIE = 'compendio_lang';

/**
 * A language is data.
 *
 * Adding one means adding a catalog here and a row in the server's supported-language list — not a
 * pass over every screen. Keys are semantic paths, never English sentences: English-as-key makes
 * every copy tweak a breaking change across all locales.
 */
const resources = {
  en: { translation: en },
  es: { translation: es },
} as const;

/**
 * Resolves the UI language: `?lang=` → cookie → `Accept-Language` → server default → `en`.
 *
 * The same chain runs server-side, so an API error comes back in the language the SPA is already
 * rendering. The user's profile preference is applied by the server, which sets the cookie.
 */
export function resolveLanguage(serverDefault: string): string {
  const fromQuery = new URLSearchParams(window.location.search).get('lang');
  if (fromQuery && isSupported(fromQuery)) {
    return normalize(fromQuery);
  }

  const fromCookie = readCookie(LANGUAGE_COOKIE);
  if (fromCookie && isSupported(fromCookie)) {
    return normalize(fromCookie);
  }

  for (const candidate of navigator.languages ?? []) {
    if (isSupported(candidate)) {
      return normalize(candidate);
    }
  }

  return isSupported(serverDefault) ? normalize(serverDefault) : 'en';
}

export async function initI18n(serverDefault: string): Promise<void> {
  await i18next.use(initReactI18next).init({
    resources,
    lng: resolveLanguage(serverDefault),
    // A safety net, not a strategy: a missing key is treated as a bug and fails CI.
    fallbackLng: 'en',
    interpolation: { escapeValue: false },
    returnNull: false,
  });
}

export function changeLanguage(language: string): void {
  void i18next.changeLanguage(language);
  document.documentElement.lang = language;
  document.cookie = `${LANGUAGE_COOKIE}=${language};path=/;max-age=31536000;samesite=strict`;
}

function isSupported(candidate: string): boolean {
  const primary = candidate.split('-')[0]?.toLowerCase() ?? '';
  return SUPPORTED_LANGUAGES.some((l) => l === primary);
}

function normalize(candidate: string): string {
  return candidate.split('-')[0]!.toLowerCase();
}

function readCookie(name: string): string | null {
  const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`));
  return match?.[1] ? decodeURIComponent(match[1]) : null;
}

export default i18next;
