import { useCallback, useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';

import { api, ApiError, isAbort, type AiBudget } from '../../lib/api';

/**
 * Whether AI exists, which of its features are on, and what is left of the caller's allowance.
 *
 * Every AI control in the product renders from this and nothing else. With no provider configured
 * the server returns `enabled: false` and every AI action returns 404, so a control rendered without
 * asking would be a control that fails when pressed — which is worse than no control at all.
 *
 * Cached for the session: the answer changes only when an administrator changes it, and asking on
 * every page render would put a request behind every navigation for a feature most instances do not
 * have turned on. The allowance rides along on the same answer, and is refetched after each action
 * rather than polled — see {@link useAiAction}.
 */
export function useAiStatus() {
  const status = useQuery({
    queryKey: ['ai', 'status'],
    queryFn: api.aiStatus,
    staleTime: 5 * 60 * 1000,
    // A failure means "no AI", not a broken screen. The whole product works without it.
    retry: false,
  });

  const enabled = status.data?.enabled === true;
  const budget = status.data?.budget;

  return {
    /** False while loading, so nothing flashes into view and then disappears. */
    enabled,
    endpointLabel: status.data?.endpointLabel ?? '',
    model: status.data?.model ?? '',
    has: (feature: string) => enabled && (status.data?.features.includes(feature) ?? false),
    isPending: status.isPending,
    budget,
    /**
     * Whether the allowance is worth mentioning before it runs out.
     *
     * Silent until it matters: a person with forty of fifty left does not need a counter, and a
     * number shown all the time reads as a warning rather than as information. Under a fifth left,
     * or five requests, is the point at which knowing changes what somebody does next.
     */
    lowBudget: isLow(budget),
  };
}

function isLow(budget: AiBudget | undefined): boolean {
  if (!budget || budget.limit <= 0 || budget.remaining === null) {
    return false;
  }

  return budget.remaining <= Math.max(5, Math.ceil(budget.limit / 5));
}

/** The feature names the server knows. Kept in one place so a typo cannot silently hide a button. */
export const aiFeatures = {
  improve: 'improve',
  draft: 'draft',
  summarize: 'summarize',
  translate: 'translate',
  ask: 'ask',
  freshness: 'freshness',
} as const;

/**
 * Runs one AI request with a cancel button behind it.
 *
 * @remarks
 * Hand-rolled rather than `useMutation`, for one reason: cancelling has to abort the HTTP request,
 * not just stop listening to it. React Query's `mutate` has no signal to hand the caller, so a
 * "cancel" built on it would leave the provider generating tokens the user has already paid for and
 * decided they do not want.
 *
 * A cancel resolves to nothing rather than to an error. The user asked for it, so there is nothing
 * to report — and rendering "AbortError" at somebody who pressed Cancel would be the machine
 * complaining about being obeyed.
 */
export function useAiAction<T>() {
  const queryClient = useQueryClient();
  const controller = useRef<AbortController | null>(null);

  const [pending, setPending] = useState(false);
  const [error, setError] = useState<ApiError | Error | null>(null);

  const run = useCallback(
    async (call: (signal: AbortSignal) => Promise<T>): Promise<T | undefined> => {
      // A second press supersedes the first rather than racing it.
      controller.current?.abort();

      const active = new AbortController();
      controller.current = active;

      setPending(true);
      setError(null);

      try {
        return await call(active.signal);
      } catch (failure) {
        if (!isAbort(failure)) {
          setError(failure instanceof Error ? failure : new Error(String(failure)));
        }

        return undefined;
      } finally {
        if (controller.current === active) {
          controller.current = null;
          setPending(false);
        }

        // Every action spends allowance, including one that failed at the provider — so the counter
        // is refetched either way rather than decremented optimistically on success.
        void queryClient.invalidateQueries({ queryKey: ['ai', 'status'] });
      }
    },
    [queryClient],
  );

  const cancel = useCallback(() => {
    controller.current?.abort();
    controller.current = null;
    setPending(false);
  }, []);

  /**
   * Navigating away cancels.
   *
   * Without this, closing the editor mid-request leaves the model generating an answer nobody will
   * ever see — against an endpoint that may be charging for it — and the `setPending` in the
   * `finally` lands on a component that is gone. Unmounting is the user saying they are done with
   * this at least as clearly as pressing Cancel is.
   */
  useEffect(() => () => controller.current?.abort(), []);

  const clearError = useCallback(() => setError(null), []);

  return { run, cancel, pending, error, clearError };
}
