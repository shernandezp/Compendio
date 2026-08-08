import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQueryClient } from '@tanstack/react-query';
import { Button, Group, Menu, Modal, Stack, Text, Tooltip } from '@mantine/core';

import { api, encodePath, type AiProposal, type Page } from '../../lib/api';
import { aiFeatures, useAiAction, useAiStatus } from './useAi';
import { AiBudgetNotice, AiFailure, notifyAiFailure, useAiResetLabel } from './AiNotices';
import { AiProposalDialog, type AiScope } from './AiProposalDialog';

export type { AiScope };

/**
 * The AI actions available on a page.
 *
 * Renders **nothing at all** when no provider is configured — not a disabled button, not a greyed
 * menu. That is the acceptance criterion, and it is also the honest presentation: the feature does
 * not exist on this instance.
 *
 * Every result comes back as a proposal in a dialog the user accepts or discards. Nothing the model
 * produces reaches the disk without a human deciding it should.
 */
export function AiMenu({
  path,
  selection,
  onAccept,
}: {
  path: string;
  /**
   * The user's current highlight, as a flag plus a way to read it.
   *
   * @remarks
   * Split in two on purpose. `active` decides the menu label and changes rarely; `read` is called at
   * the moment the action is invoked, so what gets sent is what was highlighted when the user
   * pressed the button rather than a snapshot taken at some earlier render. Passing the text itself
   * as a prop would mean serializing the selection on every keystroke to keep it current.
   *
   * A selection changes both the label and the request: the model is sent that paragraph rather than
   * the whole page, which is faster, cheaper, and — for a hosted endpoint — a good deal less of
   * somebody's HR policy leaving the building.
   */
  selection?: { active: boolean; read: () => string };
  /**
   * Applies a proposal. Absent — on the read view, where there is nothing to apply it to — the
   * dialog offers no "use this" button rather than one that silently does nothing.
   *
   * `scope` tells the editor whether to replace the highlighted range or the whole document. Getting
   * this wrong in the quiet direction — replacing a page with a rewritten paragraph — is the kind of
   * data loss the undo history exists for and nobody should need.
   */
  onAccept?: (markdown: string, scope: AiScope) => void;
}) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const ai = useAiStatus();
  const action = useAiAction<AiProposal>();
  const translation = useAiAction<Page>();

  const [proposal, setProposal] = useState<{ value: AiProposal; scope: AiScope } | null>(null);
  const [translating, setTranslating] = useState(false);

  const highlighted = selection?.active === true;

  /**
   * The menu's own failures are reported out of band; the translate dialog's stay inside it.
   *
   * A dialog has room for an alert and a reason to keep it on screen while the user decides what to
   * do. The menu has neither — it is one control in a row of them, and by the time a failure arrives
   * the dropdown has long since closed.
   */
  const { error: actionError, clearError: clearActionError } = action;
  const resetLabel = useAiResetLabel();

  useEffect(() => {
    if (actionError) {
      notifyAiFailure(actionError, t('app.error.generic'), resetLabel);
      clearActionError();
    }
  }, [actionError, clearActionError, resetLabel, t]);

  /**
   * Runs an action that can work on a highlight, reading the highlight at the moment of the click.
   *
   * If it comes back empty — the user cleared it between opening the menu and choosing an item — the
   * action falls back to the whole page and the proposal is scoped to match. The failure this avoids
   * is the quiet one: a page-sized rewrite accepted into a selection-sized hole, or the reverse.
   */
  async function runScoped(call: (text: string | undefined, signal: AbortSignal) => Promise<AiProposal>) {
    const text = highlighted ? selection?.read() : undefined;
    const scope: AiScope = text ? 'selection' : 'page';

    const result = await action.run((signal) => call(text || undefined, signal));

    if (result) {
      setProposal({ value: result, scope });
    }
  }

  async function run(call: (signal: AbortSignal) => Promise<AiProposal>) {
    const result = await action.run(call);

    if (result) {
      setProposal({ value: result, scope: 'page' });
    }
  }

  async function translate(language: string) {
    const page = await translation.run((signal) => api.aiTranslate(path, language, signal));

    if (!page) {
      return;
    }

    setTranslating(false);
    await queryClient.invalidateQueries({ queryKey: ['tree'] });
    navigate(`/p/${encodePath(page.path)}`);
  }

  if (!ai.enabled) {
    return null;
  }

  return (
    <>
      {/* While a request is in flight the trigger becomes the way out of it. The menu lives in a
          `wrap="nowrap"` button row on both screens that use it, so a separate cancel control beside
          it would squeeze its neighbours — and a spinner with no way out for the full two-minute
          timeout is what makes people reload the page and lose their buffer. */}
      {action.pending ? (
        <Tooltip label={t('ai.working', { endpoint: ai.endpointLabel })}>
          <Button variant="light" size="xs" color="gray" onClick={action.cancel}>
            {t('app.cancel')}
          </Button>
        </Tooltip>
      ) : (
        <Menu position="bottom-end" withinPortal>
          <Menu.Target>
            <Button variant="light" size="xs">
              {t('ai.menu')}
            </Button>
          </Menu.Target>

          <Menu.Dropdown>
            {/* Named where the action is invoked, not buried in a settings page: for this audience
                "where does my HR policy go" is what decides whether the feature gets used at all. */}
            <Menu.Label>{t('ai.sentTo', { endpoint: ai.endpointLabel })}</Menu.Label>

            {/* Improve rewrites the page, so it only appears where there is something to rewrite
                into. On the read view it would produce a proposal with nowhere to go. */}
            {onAccept && ai.has(aiFeatures.improve) && (
              <Menu.Item onClick={() => void runScoped((text, signal) => api.aiImprove(path, text, signal))}>
                {highlighted ? t('ai.improveSelection') : t('ai.improvePage')}
              </Menu.Item>
            )}
            {ai.has(aiFeatures.summarize) && (
              <Menu.Item onClick={() => void runScoped((text, signal) => api.aiSummarize(path, text, signal))}>
                {highlighted ? t('ai.summarizeSelection') : t('ai.summarize')}
              </Menu.Item>
            )}
            {/* Freshness reads dates and version numbers scattered through a whole procedure, so it
                takes the page even when a paragraph is highlighted. */}
            {ai.has(aiFeatures.freshness) && (
              <Menu.Item onClick={() => void run((signal) => api.aiFreshness(path, signal))}>
                {t('ai.freshness')}
              </Menu.Item>
            )}

            {ai.has(aiFeatures.translate) && (
              <>
                <Menu.Divider />
                <Menu.Item onClick={() => setTranslating(true)}>{t('ai.translate')}</Menu.Item>
              </>
            )}

            {ai.lowBudget && (
              <>
                <Menu.Divider />
                <AiBudgetNotice budget={ai.budget} />
              </>
            )}
          </Menu.Dropdown>
        </Menu>
      )}

      <Modal
        opened={translating}
        onClose={() => {
          translation.cancel();
          setTranslating(false);
        }}
        title={t('ai.translate')}
      >
        <Stack gap="md">
          {/* Said before the button, not after: what comes back is unreviewed by construction. */}
          <Text size="sm" c="dimmed">
            {t('ai.translateNote')}
          </Text>

          <Group>
            {['es', 'en'].map((language) => (
              <Button
                key={language}
                variant="light"
                loading={translation.pending}
                onClick={() => void translate(language)}
              >
                {language === 'es' ? 'Español' : 'English'}
              </Button>
            ))}

            {translation.pending && (
              <Button size="compact-sm" variant="subtle" onClick={translation.cancel}>
                {t('app.cancel')}
              </Button>
            )}
          </Group>

          <AiFailure error={translation.error} onClose={translation.clearError} />
        </Stack>
      </Modal>

      <AiProposalDialog
        proposal={proposal?.value ?? null}
        scope={proposal?.scope}
        onClose={() => setProposal(null)}
        onChange={(markdown) =>
          setProposal((current) => (current ? { ...current, value: { ...current.value, proposal: markdown } } : current))
        }
        // Threaded through rather than closed over, so the scope that produced the proposal is the
        // scope it is applied with even if the user's highlight has changed since.
        onAccept={onAccept && ((markdown) => {
          onAccept(markdown, proposal?.scope ?? 'page');
          setProposal(null);
        })}
      />
    </>
  );
}
