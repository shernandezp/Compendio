import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Alert, Box, Button, Group, Paper, ScrollArea, Stack, Text } from '@mantine/core';

/**
 * The three-pane merge.
 *
 * A conflict is the one moment a user could lose an hour's work, so it gets real UI: their version,
 * mine, and a result that starts as theirs and takes either side per differing block. An alert box
 * saying "somebody else saved" would make the user re-type.
 *
 * Desktop only, by decision — this does not fit 360 px, and a cramped merge is how work gets lost.
 */
export function ConflictResolver({
  mine,
  theirs,
  onResolve,
  onCancel,
}: {
  mine: string;
  theirs: string;
  onResolve: (merged: string) => void | Promise<void>;
  onCancel: () => void;
}) {
  const { t } = useTranslation();

  const hunks = useMemo(() => buildHunks(theirs, mine), [theirs, mine]);
  const [choices, setChoices] = useState<Record<number, 'mine' | 'theirs'>>({});

  // Joined with a blank line, because that is what separated the blocks in the first place. Joining
  // with a single newline would run every paragraph, list and table into its neighbour — a merge
  // that silently reformats the document is not a merge anybody can trust.
  const merged = `${hunks
    .map((hunk, index) => (hunk.same ? hunk.theirs : choices[index] === 'mine' ? hunk.mine : hunk.theirs))
    .filter((block) => block.trim().length > 0)
    .join('\n\n')}\n`;

  return (
    <Stack>
      <Alert variant="light">{t('editor.conflict.explain')}</Alert>

      <Group align="stretch" grow wrap="nowrap">
        <Pane title={t('editor.conflict.theirs')} text={theirs} />
        <Pane title={t('editor.conflict.mine')} text={mine} />
      </Group>

      <Text size="sm" fw={600}>
        {t('history.compare')}
      </Text>

      <ScrollArea h={280}>
        <Stack gap={4}>
          {hunks.map((hunk, index) =>
            hunk.same ? (
              <Box key={index} px="xs" style={{ whiteSpace: 'pre-wrap', fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 13 }}>
                {hunk.theirs}
              </Box>
            ) : (
              <Paper key={index} withBorder p="xs">
                <Group justify="space-between" wrap="nowrap" align="flex-start">
                  <Box style={{ whiteSpace: 'pre-wrap', fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 13 }}>
                    {choices[index] === 'mine' ? hunk.mine : hunk.theirs}
                  </Box>
                  <Group gap={4} wrap="nowrap">
                    <Button
                      size="compact-xs"
                      variant={choices[index] === 'theirs' || !choices[index] ? 'filled' : 'subtle'}
                      onClick={() => setChoices((c) => ({ ...c, [index]: 'theirs' }))}
                    >
                      {t('editor.conflict.takeTheirs')}
                    </Button>
                    <Button
                      size="compact-xs"
                      variant={choices[index] === 'mine' ? 'filled' : 'subtle'}
                      onClick={() => setChoices((c) => ({ ...c, [index]: 'mine' }))}
                    >
                      {t('editor.conflict.takeMine')}
                    </Button>
                  </Group>
                </Group>
              </Paper>
            ),
          )}
        </Stack>
      </ScrollArea>

      <Group justify="flex-end">
        <Button variant="default" onClick={onCancel}>
          {t('app.cancel')}
        </Button>
        <Button onClick={() => void onResolve(merged)}>{t('app.save')}</Button>
      </Group>
    </Stack>
  );
}

function Pane({ title, text }: { title: string; text: string }) {
  return (
    <Paper withBorder p="xs">
      <Text size="xs" fw={700} tt="uppercase" c="dimmed" mb={4}>
        {title}
      </Text>
      <ScrollArea h={200}>
        <Box style={{ whiteSpace: 'pre-wrap', fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 12 }}>
          {text}
        </Box>
      </ScrollArea>
    </Paper>
  );
}

interface Hunk {
  same: boolean;
  theirs: string;
  mine: string;
}

/**
 * Block-level hunks, split on blank lines.
 *
 * A line-level merge on prose produces hunks nobody can read; a paragraph is the unit a person
 * actually decides about.
 */
function buildHunks(theirs: string, mine: string): Hunk[] {
  const left = theirs.split(/\n{2,}/);
  const right = mine.split(/\n{2,}/);
  const hunks: Hunk[] = [];

  const length = Math.max(left.length, right.length);
  for (let index = 0; index < length; index++) {
    const l = left[index] ?? '';
    const r = right[index] ?? '';
    hunks.push({ same: l === r, theirs: l, mine: r });
  }

  return hunks;
}
