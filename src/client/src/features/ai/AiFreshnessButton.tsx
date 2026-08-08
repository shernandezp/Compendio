import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ActionIcon, Tooltip } from '@mantine/core';
import { IconSparkles, IconX } from '@tabler/icons-react';

import { api, type AiProposal } from '../../lib/api';
import { useAiAction } from './useAi';
import { notifyAiFailure, useAiResetLabel } from './AiNotices';
import { AiProposalDialog } from './AiProposalDialog';

/**
 * "Does this page look out of date?", asked from a list rather than from the page itself.
 *
 * @remarks
 * <p>
 * The stale report already knows which pages are <em>overdue for review</em>; this answers the next
 * question, which is whether the content has actually rotted — a version number, a server name, a
 * date in a procedure. Opening fifty pages one at a time to find out is the reason the report exists,
 * so the action belongs beside the row.
 * </p>
 * <p>
 * Deliberately not the full {@link AiMenu}: fifty dropdowns offering translation and rewriting would
 * turn a report into a control panel, and there is no editor here for a rewrite to land in. One icon,
 * one action, a read-only result.
 * </p>
 * <p>
 * It is not rendered at all unless the caller has already established the feature is on — the parent
 * checks once and drops the whole column, rather than every row asking the same question and each one
 * rendering an empty cell.
 * </p>
 */
export function AiFreshnessButton({ path, endpointLabel }: { path: string; endpointLabel: string }) {
  const { t } = useTranslation();
  const action = useAiAction<AiProposal>();
  const [proposal, setProposal] = useState<AiProposal | null>(null);

  const { error, clearError } = action;
  const resetLabel = useAiResetLabel();

  // A table row has no room for an alert, and by the time a failure arrives the reader may have
  // scrolled past the row that caused it.
  useEffect(() => {
    if (error) {
      notifyAiFailure(error, t('app.error.generic'), resetLabel);
      clearError();
    }
  }, [error, clearError, resetLabel, t]);

  async function check() {
    const result = await action.run((signal) => api.aiFreshness(path, signal));

    if (result) {
      setProposal(result);
    }
  }

  const label = action.pending
    ? t('ai.working', { endpoint: endpointLabel })
    : t('ai.freshness');

  return (
    <>
      <Tooltip label={label}>
        {/* The same control cancels, for the same reason it does in the menu: one cell wide, and a
            row that spins for two minutes with no way out is worse than no button. */}
        <ActionIcon
          variant="subtle"
          size="sm"
          color={action.pending ? 'gray' : undefined}
          aria-label={label}
          onClick={() => (action.pending ? action.cancel() : void check())}
        >
          {action.pending ? <IconX size={16} /> : <IconSparkles size={16} />}
        </ActionIcon>
      </Tooltip>

      <AiProposalDialog
        proposal={proposal}
        title={t('ai.freshnessResult')}
        onClose={() => setProposal(null)}
      />
    </>
  );
}
