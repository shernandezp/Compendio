import { useTranslation } from 'react-i18next';
import { Button, Group, Modal, ScrollArea, Stack, Text, Textarea } from '@mantine/core';

import { type AiProposal } from '../../lib/api';

/** What a proposal was made from, so accepting it puts the answer back where it came from. */
export type AiScope = 'page' | 'selection';

/**
 * Everything a model produced, before any of it reaches the disk.
 *
 * @remarks
 * <p>
 * Shared by every surface that asks for a proposal, because the promise it makes is the one thing in
 * the AI feature that must never differ between two screens: <em>nothing is saved until you accept</em>.
 * Two copies of this dialog would be two chances for one of them to stop being true.
 * </p>
 * <p>
 * Editable when there is somewhere to put it, read-only when there is not. A freshness hint on the
 * stale report or a summary on the read view has no destination, so offering a text box that invites
 * edits nobody can keep would be a small lie about what the screen does.
 * </p>
 */
export function AiProposalDialog({
  proposal,
  scope = 'page',
  title,
  onChange,
  onAccept,
  onClose,
}: {
  /** Null closes the dialog. */
  proposal: AiProposal | null;
  scope?: AiScope;
  /** Overrides the heading, for an action whose output is not a suggested edit. */
  title?: string;
  onChange?: (markdown: string) => void;
  /** Absent means read-only: there is nowhere to apply this. */
  onAccept?: (markdown: string) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation();

  const heading = title
    ?? (scope === 'selection' ? t('ai.proposalTitleSelection') : t('ai.proposalTitle'));

  return (
    <Modal opened={proposal !== null} onClose={onClose} title={heading} size="lg">
      <Stack gap="md">
        {/* Which model, which endpoint, and that nothing has happened yet. Said before the buttons,
            because it is what the reader needs in order to judge what they are looking at. */}
        <Text size="sm" c="dimmed">
          {t(onAccept ? 'ai.proposalNote' : 'ai.proposalNoteReadOnly', {
            model: proposal?.model,
            endpoint: proposal?.endpointLabel,
          })}
        </Text>

        <ScrollArea.Autosize mah={400}>
          <Textarea
            value={proposal?.proposal ?? ''}
            onChange={(event) => onChange?.(event.currentTarget.value)}
            readOnly={!onAccept}
            autosize
            minRows={8}
            aria-label={heading}
          />
        </ScrollArea.Autosize>

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {onAccept ? t('common.discard') : t('common.close')}
          </Button>

          {/* Only where there is somewhere to put it. A button that quietly did nothing would be
              worse than its absence. */}
          {onAccept && proposal && (
            <Button onClick={() => onAccept(proposal.proposal)}>
              {scope === 'selection' ? t('ai.acceptSelection') : t('ai.accept')}
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
