import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Group,
  Modal,
  Radio,
  ScrollArea,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { IconAlertTriangle, IconFolder, IconLock } from '@tabler/icons-react';

import { api, ApiError, type TreeNode } from '../../lib/api';
import { moveDraft } from '../editor/drafts';

const MARKDOWN = /\.md$/i;

/**
 * A name is one path segment.
 *
 * The server splits on `/` before it validates, so "reports/q3" in this field is not an illegal
 * name — it is a *different destination* from the one selected just below, quietly created one
 * level down. The picker has to be the only thing that decides where the page lands.
 */
const SEPARATOR = /[/\\]/;

/**
 * What PathPolicy rejects, checked here so the message arrives while the field is still focused
 * rather than as a round trip. Spaces and hyphens are fine; a *trailing* dot or space is not,
 * because Windows strips it and "report .md" then names the same file as "report.md ".
 */
const ILLEGAL = /[<>:"|?*]|[\x00-\x1f]|\.\.|[. ]$/;

/** PathPolicy.MaxSegmentLength. */
const MAX_NAME = 100;

/**
 * Move or rename, for a page or a folder.
 *
 * A destination picker rather than drag-and-drop. Dropping one node onto another in a tree this
 * deep is a fine gesture with a mouse and an unusable one with a finger or a keyboard, and the
 * thing being moved is somebody's document — a gesture that misses by one row and silently files a
 * runbook under the wrong folder is worse than a dialog that takes two more seconds.
 *
 * Both ends are permission-checked by the server, so this picker is a courtesy, not the control:
 * it greys out what cannot receive the node so nobody discovers it by way of a 403.
 */
export function MoveDialog({
  node,
  tree,
  onClose,
  onMoved,
}: {
  node: TreeNode;
  tree: TreeNode[];
  onClose: () => void;
  /** The new path, so the caller can follow the page if it was the one on screen. */
  onMoved: (newPath: string) => void;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const extension = node.isFolder ? '' : (node.path.match(MARKDOWN)?.[0] ?? '');
  const currentFolder = node.path.includes('/') ? node.path.slice(0, node.path.lastIndexOf('/')) : '';
  const currentName = node.name.replace(MARKDOWN, '');

  const [name, setName] = useState(currentName);
  const [destination, setDestination] = useState(currentFolder);
  const [failure, setFailure] = useState<string | null>(null);

  // A folder cannot be moved inside itself: the destination list simply does not offer its own
  // subtree, which is clearer than an error after the fact.
  const forbidden = node.isFolder ? `${node.path}/` : null;
  const folders = useMemo(
    () => flattenFolders(tree).filter((f) => f.path !== node.path && !(forbidden && f.path.startsWith(forbidden))),
    [tree, node.path, forbidden],
  );

  const trimmed = name.trim();
  const targetPath = destination ? `${destination}/${trimmed}${extension}` : `${trimmed}${extension}`;
  const unchanged = targetPath === node.path;

  const invalid =
    trimmed.length === 0
      ? null
      : SEPARATOR.test(trimmed)
        ? t('page.moveNameSeparator')
        : ILLEGAL.test(trimmed) || trimmed.length > MAX_NAME
          ? t('page.moveNameIllegal')
          : null;

  const move = useMutation({
    mutationFn: async () => {
      // The page endpoint returns the moved page and the folder one returns nothing; neither
      // result is used, because the tree refetch below is what the screen actually reads.
      if (node.isFolder) {
        await api.moveFolder(node.path, targetPath);
      } else {
        await api.movePage(node.path, targetPath);
      }
    },
    onSuccess: async () => {
      if (!node.isFolder) {
        moveDraft(node.path, targetPath);
      }

      await queryClient.invalidateQueries({ queryKey: ['tree'] });
      onMoved(targetPath);
      onClose();
    },
    onError: (error) => {
      // The three failures worth naming: something is already there, the destination is not yours
      // to write to, and the name is one the content folder cannot hold. Anything else gets the
      // generic message rather than a guess.
      if (error instanceof ApiError && error.code === 'path.exists') {
        setFailure(t('page.moveExists'));
      } else if (error instanceof ApiError && error.status === 403) {
        setFailure(t('page.moveForbidden'));
      } else if (error instanceof ApiError && error.code === 'path.invalid') {
        setFailure(t('page.moveNameIllegal'));
      } else {
        setFailure(t('app.error.generic'));
      }
    },
  });

  const blocked = trimmed.length === 0 || unchanged || invalid !== null;

  function submit() {
    if (blocked) {
      return;
    }

    setFailure(null);
    move.mutate();
  }

  return (
    <Modal opened onClose={onClose} title={t('page.move')} size="md">
      <Stack>
        <TextInput
          label={t('page.moveName')}
          value={name}
          error={invalid}
          onChange={(event) => setName(event.currentTarget.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              submit();
            }
          }}
          rightSection={
            extension ? (
              <Text size="sm" c="dimmed" pr="xs">
                {extension}
              </Text>
            ) : null
          }
          rightSectionWidth={44}
          data-autofocus
        />

        <Box>
          <Text size="sm" fw={500} mb={4}>
            {t('page.moveDestination')}
          </Text>
          <ScrollArea.Autosize mah={280} type="auto">
            <Radio.Group value={destination} onChange={setDestination}>
              <Stack gap={2}>
                <DestinationRow path="" label={t('page.moveRoot')} depth={0} isCurrent={currentFolder === ''} />
                {folders.map((folder) => (
                  <DestinationRow
                    key={folder.path}
                    path={folder.path}
                    label={folder.name}
                    depth={folder.depth}
                    disabled={!folder.canAccept}
                    isSecure={folder.isSecure}
                    isCurrent={folder.path === currentFolder}
                  />
                ))}
              </Stack>
            </Radio.Group>
          </ScrollArea.Autosize>
        </Box>

        {failure && (
          <Alert color="red" icon={<IconAlertTriangle size={18} />}>
            {failure}
          </Alert>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t('app.cancel')}
          </Button>
          <Button onClick={submit} loading={move.isPending} disabled={blocked}>
            {t('page.moveSubmit')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function DestinationRow({
  path,
  label,
  depth,
  disabled,
  isSecure,
  isCurrent,
}: {
  path: string;
  label: string;
  depth: number;
  disabled?: boolean;
  isSecure?: boolean;
  isCurrent?: boolean;
}) {
  const { t } = useTranslation();

  return (
    <Radio
      value={path}
      disabled={disabled}
      pl={depth * 20}
      label={
        <Group gap={6} wrap="nowrap">
          {isSecure ? <IconLock size={14} /> : <IconFolder size={14} />}
          <Text size="sm" span>
            {label}
          </Text>
          {isCurrent && (
            <Text size="xs" c="dimmed" span>
              {t('page.moveCurrent')}
            </Text>
          )}
          {disabled && (
            <Text size="xs" c="dimmed" span>
              {t('page.moveNoWrite')}
            </Text>
          )}
        </Group>
      }
    />
  );
}

/** Depth-first, so the list reads in the same order and shape as the tree it came from. */
function flattenFolders(
  nodes: TreeNode[],
  depth = 1,
): { path: string; name: string; depth: number; canAccept: boolean; isSecure: boolean }[] {
  return nodes
    .filter((node) => node.isFolder)
    .flatMap((node) => [
      {
        path: node.path,
        name: node.name,
        depth,
        canAccept: node.level === 'Write' || node.level === 'Manage',
        isSecure: node.isSecure,
      },
      ...flattenFolders(node.children, depth + 1),
    ]);
}
