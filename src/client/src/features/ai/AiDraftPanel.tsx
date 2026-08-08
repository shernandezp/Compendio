import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Button, Group, Modal, Select, Stack, Text, Textarea } from '@mantine/core';
import { IconSparkles } from '@tabler/icons-react';

import { api, type AiProposal } from '../../lib/api';
import { aiFeatures, useAiAction, useAiStatus } from './useAi';
import { AiBudgetNotice, AiFailure } from './AiNotices';

/**
 * Rough notes into a structured page, on the screen where a page is being started.
 *
 * @remarks
 * <p>
 * This is the AI feature with the clearest case for existing at all. The wikis this product competes
 * with die because the warehouse supervisor who knows the procedure will not sit down to a blank
 * page and a heading structure — but they will type six lines of what they do. Turning that into an
 * SOP shaped like the organization's own template is the difference between the knowledge being
 * written down and not.
 * </p>
 * <p>
 * It offers the same template catalogue the editor's picker reads, so a draft comes out shaped like
 * the pages already in the wiki rather than like a second, hard-coded idea of a procedure.
 * </p>
 * <p>
 * As with every other AI action the result is a proposal: it lands in the editor buffer, unsaved,
 * for a person to read and change before anything reaches the disk.
 * </p>
 */
export function AiDraftPanel({
  folderPath,
  onDraft,
}: {
  /** Where the new page will land. Empty for the root, which the API reads as the content root. */
  folderPath: string;
  /** Hands the draft to the editor buffer. Never saves — that stays the user's decision. */
  onDraft: (markdown: string) => void;
}) {
  const { t } = useTranslation();
  const ai = useAiStatus();
  const action = useAiAction<AiProposal>();

  const [opened, setOpened] = useState(false);
  const [bullets, setBullets] = useState('');
  const [templateId, setTemplateId] = useState<string | null>(null);

  const templates = useQuery({
    queryKey: ['templates'],
    queryFn: api.templates,
    staleTime: 5 * 60 * 1000,
    // Only asked for once the panel is open: on an instance with no AI this request would be a
    // round trip for a control that never renders.
    enabled: opened,
  });

  if (!ai.has(aiFeatures.draft)) {
    return null;
  }

  async function draft() {
    const result = await action.run((signal) =>
      api.aiDraft(folderPath, bullets, templateId ?? undefined, signal));

    if (result) {
      onDraft(result.proposal);
      setOpened(false);
      setBullets('');
    }
  }

  return (
    <>
      <Button
        variant="light"
        size="xs"
        leftSection={<IconSparkles size={16} />}
        onClick={() => setOpened(true)}
      >
        {t('ai.draft')}
      </Button>

      <Modal
        opened={opened}
        onClose={() => {
          action.cancel();
          setOpened(false);
        }}
        title={t('ai.draftTitle')}
        size="lg"
      >
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            {t('ai.draftIntro', { endpoint: ai.endpointLabel })}
          </Text>

          <Textarea
            value={bullets}
            onChange={(event) => setBullets(event.currentTarget.value)}
            label={t('ai.draftNotes')}
            description={t('ai.draftNotesHint')}
            placeholder={t('ai.draftPlaceholder')}
            autosize
            minRows={6}
            maxRows={14}
            data-autofocus
          />

          <Select
            label={t('ai.draftTemplate')}
            description={t('ai.draftTemplateHint')}
            value={templateId}
            onChange={setTemplateId}
            clearable
            data={(templates.data ?? [])
              // "Blank" is the absence of a template, which this control already expresses by being
              // empty. Offering both would be two ways to say the same thing.
              .filter((template) => template.id !== 'blank')
              .map((template) => ({
                value: template.id,
                // Bundled titles are i18n keys; an organization's own overrides are literal text,
                // and fall through unchanged.
                label: t(template.title, { defaultValue: template.title }),
              }))}
          />

          {ai.lowBudget && <AiBudgetNotice budget={ai.budget} />}

          <AiFailure error={action.error} onClose={action.clearError} />

          <Group justify="space-between">
            <Text size="xs" c="dimmed">
              {t('ai.draftUnsavedNote')}
            </Text>

            <Group gap="xs">
              {action.pending && (
                <Button variant="subtle" onClick={action.cancel}>
                  {t('app.cancel')}
                </Button>
              )}

              <Button
                onClick={() => void draft()}
                loading={action.pending}
                disabled={bullets.trim().length === 0}
                leftSection={<IconSparkles size={16} />}
              >
                {t('ai.draft')}
              </Button>
            </Group>
          </Group>

          {action.pending && (
            <Text size="sm" c="dimmed">
              {t('ai.working', { endpoint: ai.endpointLabel })}
            </Text>
          )}
        </Stack>
      </Modal>
    </>
  );
}
