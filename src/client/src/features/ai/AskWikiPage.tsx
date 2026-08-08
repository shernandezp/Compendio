import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { Alert, Anchor, Button, Card, Group, Stack, Text, Textarea, Title } from '@mantine/core';

import { api, encodePath, type AiAnswer } from '../../lib/api';
import { aiFeatures, useAiAction, useAiStatus } from './useAi';
import { AiBudgetNotice, AiFailure } from './AiNotices';

/**
 * Question answering over the wiki, with linked sources.
 *
 * Every source shown here survived two checks on the server: retrieval filtered by the asker's
 * readable folders before any passage was read from disk, and every path the model cited was checked
 * again before the answer was sent. The screen renders what it was given and resolves nothing
 * itself.
 *
 * An answer with no sources is shown as exactly that, rather than as prose. The retriever is BM25,
 * so "nothing relevant was found" is an ordinary outcome, and dressing it up would turn a retrieval
 * miss into something that reads like knowledge.
 */
export function AskWikiPage() {
  const { t } = useTranslation();
  const ai = useAiStatus();
  const ask = useAiAction<AiAnswer>();

  const [question, setQuestion] = useState('');
  const [answer, setAnswer] = useState<AiAnswer | null>(null);

  async function submit() {
    if (question.trim().length === 0) {
      return;
    }

    // Cleared first: leaving the previous answer on screen under a spinner invites reading it as the
    // response to the question just asked.
    setAnswer(null);

    const result = await ask.run((signal) => api.aiAsk(question, signal));

    if (result) {
      setAnswer(result);
    }
  }

  if (!ai.isPending && !ai.has(aiFeatures.ask)) {
    return (
      <Alert color="gray" title={t('ai.unavailableTitle')}>
        {t('ai.unavailableBody')}
      </Alert>
    );
  }

  return (
    <Stack gap="lg">
      <Title order={2}>{t('ai.askTitle')}</Title>

      <Text size="sm" c="dimmed">
        {t('ai.askIntro', { endpoint: ai.endpointLabel })}
      </Text>

      <Textarea
        value={question}
        onChange={(event) => setQuestion(event.currentTarget.value)}
        placeholder={t('ai.askPlaceholder')}
        autosize
        minRows={2}
        onKeyDown={(event) => {
          if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
            void submit();
          }
        }}
      />

      <Group>
        <Button onClick={() => void submit()} loading={ask.pending} disabled={question.trim().length === 0}>
          {t('ai.ask')}
        </Button>

        {/* Retrieval plus two model calls, against an endpoint that may be a laptop CPU. Two
            minutes of spinner with no way out is what makes people reload the page. */}
        {ask.pending && (
          <>
            <Button variant="subtle" onClick={ask.cancel}>
              {t('app.cancel')}
            </Button>
            <Text size="sm" c="dimmed">
              {t('ai.working', { endpoint: ai.endpointLabel })}
            </Text>
          </>
        )}
      </Group>

      {ai.lowBudget && <AiBudgetNotice budget={ai.budget} />}

      <AiFailure error={ask.error} onClose={ask.clearError} />

      {answer && (
        <Card withBorder padding="md">
          <Stack gap="md">
            {answer.answer.trim().length === 0 ? (
              <Text c="dimmed">{t('ai.noAnswer')}</Text>
            ) : (
              <Text style={{ whiteSpace: 'pre-wrap' }}>{answer.answer}</Text>
            )}

            {answer.citations.length > 0 && (
              <Stack gap={4}>
                <Text size="sm" fw={600}>
                  {t('ai.sources')}
                </Text>
                {answer.citations.map((citation) => (
                  <Anchor key={citation.path} component={Link} to={`/p/${encodePath(citation.path)}`} size="sm">
                    {citation.title}
                  </Anchor>
                ))}
              </Stack>
            )}

            <Text size="xs" c="dimmed">
              {t('ai.answeredBy', { model: answer.model, endpoint: answer.endpointLabel })}
            </Text>
          </Stack>
        </Card>
      )}
    </Stack>
  );
}
