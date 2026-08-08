import { useMemo, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ActionIcon,
  Button,
  Group,
  Loader,
  Menu,
  Modal,
  NavLink,
  ScrollArea,
  Stack,
  Text,
  TextInput,
  Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { modals } from '@mantine/modals';
import {
  IconChevronRight,
  IconDotsVertical,
  IconFile,
  IconFolder,
  IconFolderPlus,
  IconLock,
  IconPlus,
} from '@tabler/icons-react';

import { api, ApiError, encodePath, type TreeNode } from '../../lib/api';
import { clearDraft } from '../editor/drafts';
import { MoveDialog } from './MoveDialog';

/**
 * The navigation tree.
 *
 * Renders exactly what the API returned and decides nothing. A folder the caller cannot read never
 * arrives here in the first place — invisible rather than greyed out, because a folder name is
 * often the sensitive part and a locked placeholder invites a support ticket for every one.
 */
export function TreePanel({ onNavigate }: { onNavigate?: () => void }) {
  const { t, i18n } = useTranslation();
  const location = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const tree = useQuery({ queryKey: ['tree'], queryFn: api.tree });

  const [newFolderIn, setNewFolderIn] = useState<string | null>(null);
  const [folderName, setFolderName] = useState('');
  const [moving, setMoving] = useState<TreeNode | null>(null);
  const [renaming, setRenaming] = useState<TreeNode | null>(null);
  const [newTitle, setNewTitle] = useState('');

  const createFolder = useMutation({
    mutationFn: () => api.createFolder(newFolderIn ?? '', folderName),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['tree'] });
      setNewFolderIn(null);
      setFolderName('');
    },
    onError: () => notifications.show({ color: 'red', message: t('app.error.generic') }),
  });

  const remove = useMutation({
    mutationFn: (target: TreeNode) =>
      target.isFolder ? api.deleteFolder(target.path) : api.deletePage(target.path),
    onSuccess: async (_result, target) => {
      if (!target.isFolder) {
        clearDraft(target.path);
      }

      await queryClient.invalidateQueries({ queryKey: ['tree'] });
      notifications.show({ message: target.isFolder ? t('nav.folderDeleted') : t('page.deleted') });

      // Read from the location rather than the `currentPath` below, which is computed after an
      // early return and so is not in scope for every path through this component.
      const open = decodeURIComponent(location.pathname.replace(/^\/(p|edit|history)\//, ''));
      if (open === target.path || (target.isFolder && open.startsWith(`${target.path}/`))) {
        navigate('/dashboard', { replace: true });
      }
    },
    onError: (error) =>
      notifications.show({
        color: 'red',
        message:
          error instanceof ApiError && error.status === 403
            ? t('page.deleteForbidden')
            : t('app.error.generic'),
      }),
  });

  const rename = useMutation({
    mutationFn: (target: TreeNode) => api.setPageTitle(target.path, newTitle.trim()),
    onSuccess: async (_result, target) => {
      await queryClient.invalidateQueries({ queryKey: ['tree'] });
      await queryClient.invalidateQueries({ queryKey: ['page', target.path] });
      notifications.show({ message: t('page.titleChanged') });
      setRenaming(null);
      setNewTitle('');
    },
    onError: (error) =>
      notifications.show({
        color: 'red',
        message:
          error instanceof ApiError && error.status === 403
            ? t('page.deleteForbidden')
            : t('app.error.generic'),
      }),
  });

  function startRename(target: TreeNode) {
    setNewTitle(target.title);
    setRenaming(target);
  }

  /**
   * Deleting a folder takes everything under it, so the count goes in the question. It counts what
   * the caller can *see*: a restricted subfolder is absent from this tree but still inside the
   * folder on disk, and the server removes it. "that you can see" is the honest way to say a number
   * that is a floor rather than a total.
   */
  function confirmDelete(target: TreeNode) {
    const visiblePages = target.isFolder ? countPages(target) : 0;

    modals.openConfirmModal({
      title: target.isFolder ? t('nav.deleteFolder') : t('page.delete'),
      children: (
        <Stack gap="xs">
          <Text size="sm">
            {target.isFolder
              ? t('nav.deleteFolderConfirm', { name: target.name })
              : t('page.deleteConfirm', { title: target.title })}
          </Text>
          {visiblePages > 0 && (
            <Text size="sm" fw={500}>
              {t('nav.deleteFolderCount', { count: visiblePages })}
            </Text>
          )}
        </Stack>
      ),
      labels: { confirm: t('app.delete'), cancel: t('app.cancel') },
      confirmProps: { color: 'red' },
      onConfirm: () => remove.mutate(target),
    });
  }

  // Sorted on the client with Intl.Collator in the resolved locale, because Spanish
  // alphabetization of ñ and accents is a client concern; the API returns a stable order.
  const collator = useMemo(() => new Intl.Collator(i18n.language, { sensitivity: 'base' }), [i18n.language]);

  if (tree.isPending) {
    return <Loader size="sm" m="md" />;
  }

  const nodes = sortNodes(tree.data?.nodes ?? [], collator);
  const canWriteRoot = tree.data?.rootLevel === 'Write' || tree.data?.rootLevel === 'Manage';
  const currentPath = decodeURIComponent(location.pathname.replace(/^\/(p|edit|history)\//, ''));

  return (
    <Stack gap="xs" h="100%">
      <Group justify="space-between" px="xs">
        <Text size="xs" fw={700} tt="uppercase" c="dimmed">
          {t('nav.tree')}
        </Text>
        {/* The root itself is not a node, so these create at the root and only make sense for
            someone who can write there. Without this gate a read-only user was invited to type a
            whole page and only told at save time that they could not keep it. */}
        {canWriteRoot && (
          <Group gap={2}>
            <Tooltip label={t('nav.newFolder')}>
              <ActionIcon variant="subtle" size="sm" onClick={() => setNewFolderIn('')} aria-label={t('nav.newFolder')}>
                <IconFolderPlus size={16} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label={t('nav.newPage')}>
              <ActionIcon component={Link} to="/edit/new" variant="subtle" size="sm" aria-label={t('nav.newPage')}>
                <IconPlus size={16} />
              </ActionIcon>
            </Tooltip>
          </Group>
        )}
      </Group>

      <ScrollArea flex={1} type="auto">
        {nodes.length === 0 ? (
          <Text size="sm" c="dimmed" p="sm">
            {t('nav.emptyTree')}
          </Text>
        ) : (
          nodes.map((node) => (
            <TreeItem
              key={node.path}
              node={node}
              collator={collator}
              currentPath={currentPath}
              onNavigate={onNavigate}
              onNewFolder={setNewFolderIn}
              onMove={setMoving}
              onRename={startRename}
              onDelete={confirmDelete}
            />
          ))
        )}
      </ScrollArea>

      {moving && (
        <MoveDialog
          node={moving}
          tree={tree.data?.nodes ?? []}
          onClose={() => setMoving(null)}
          onMoved={(newPath) => {
            notifications.show({ message: t('page.moved', { path: newPath }) });

            // The page that moved may be the one on screen, and its old URL is now a 404. Only the
            // exact node is followed: a folder move changes the paths of everything under it, so an
            // open descendant is redirected by rewriting the prefix it moved with.
            if (currentPath === moving.path) {
              navigate(routeFor(location.pathname, newPath), { replace: true });
            } else if (moving.isFolder && currentPath.startsWith(`${moving.path}/`)) {
              const rest = currentPath.slice(moving.path.length);
              navigate(routeFor(location.pathname, newPath + rest), { replace: true });
            }
          }}
        />
      )}

      <Modal
        opened={newFolderIn !== null}
        onClose={() => setNewFolderIn(null)}
        title={t('nav.newFolder')}
        size="sm"
      >
        <Stack>
          {newFolderIn ? (
            <Text size="sm" c="dimmed">
              {newFolderIn}
            </Text>
          ) : null}
          <TextInput
            value={folderName}
            onChange={(event) => setFolderName(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && folderName.trim().length > 0) {
                createFolder.mutate();
              }
            }}
            data-autofocus
            aria-label={t('nav.newFolder')}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setNewFolderIn(null)}>
              {t('app.cancel')}
            </Button>
            <Button
              onClick={() => createFolder.mutate()}
              loading={createFolder.isPending}
              disabled={folderName.trim().length === 0}
            >
              {t('app.save')}
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={renaming !== null}
        onClose={() => setRenaming(null)}
        title={t('page.changeTitle')}
        size="sm"
      >
        <Stack>
          <TextInput
            label={t('page.titleLabel')}
            value={newTitle}
            onChange={(event) => setNewTitle(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && newTitle.trim().length > 0 && renaming) {
                rename.mutate(renaming);
              }
            }}
            data-autofocus
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRenaming(null)}>
              {t('app.cancel')}
            </Button>
            <Button
              onClick={() => renaming && rename.mutate(renaming)}
              loading={rename.isPending}
              disabled={newTitle.trim().length === 0}
            >
              {t('app.save')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

function TreeItem({
  node,
  collator,
  currentPath,
  onNavigate,
  onNewFolder,
  onMove,
  onRename,
  onDelete,
}: {
  node: TreeNode;
  collator: Intl.Collator;
  currentPath: string;
  onNavigate?: () => void;
  onNewFolder: (parentPath: string) => void;
  onMove: (node: TreeNode) => void;
  onRename: (node: TreeNode) => void;
  onDelete: (node: TreeNode) => void;
}) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  // Compared against the decoded path the router hands us, so no encoding here — this is a path
  // comparison, not a URL.
  const [opened, setOpened] = useState(() => currentPath.startsWith(`${node.path}/`));

  const icon = node.isSecure ? (
    <IconLock size={15} />
  ) : node.isFolder ? (
    <IconFolder size={15} />
  ) : (
    <IconFile size={15} />
  );

  // Pages inherit the level of the folder holding them, so one test answers both.
  const canWrite = node.level === 'Write' || node.level === 'Manage';
  const canManage = node.level === 'Manage';

  if (!node.isFolder) {
    return (
      <NavLink
        component={Link}
        to={`/p/${encodePath(node.path)}`}
        label={node.title}
        leftSection={icon}
        active={currentPath === node.path}
        onClick={onNavigate}
        rightSection={
          canWrite ? (
            <Menu position="right-start" withinPortal>
              <Menu.Target>
                <ActionIcon
                  variant="subtle"
                  size="xs"
                  component="div"
                  aria-label={t('app.menu')}
                  // The button lives inside the link. Stopping propagation alone would keep
                  // react-router's handler from running *and* leave the anchor's own navigation
                  // intact, which reloads the whole app — so the default goes too.
                  onClick={(event) => {
                    event.preventDefault();
                    event.stopPropagation();
                  }}
                >
                  <IconDotsVertical size={14} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item onClick={() => onRename(node)}>{t('page.changeTitle')}</Menu.Item>
                <Menu.Item onClick={() => onMove(node)}>{t('page.move')}</Menu.Item>
                <Menu.Item color="red" onClick={() => onDelete(node)}>
                  {t('page.delete')}
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          ) : null
        }
      />
    );
  }

  return (
    <NavLink
      label={node.name}
      leftSection={icon}
      rightSection={
        <Group gap={2} wrap="nowrap">
          {(canWrite || canManage) && (
            <Menu position="right-start" withinPortal>
              <Menu.Target>
                <ActionIcon
                  variant="subtle"
                  size="xs"
                  component="div"
                  aria-label={t('app.menu')}
                  onClick={(event) => event.stopPropagation()}
                >
                  <IconDotsVertical size={14} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                {canWrite && (
                  <>
                    <Menu.Item onClick={() => navigate(`/edit/new?folder=${encodeURIComponent(node.path)}`)}>
                      {t('nav.newPage')}
                    </Menu.Item>
                    <Menu.Item onClick={() => onNewFolder(node.path)}>{t('nav.newFolder')}</Menu.Item>
                  </>
                )}
                {/* Moving a folder is as destructive as deleting it from where it was, so the
                    server asks for `manage` at the source — not `write`. Offering these one level
                    lower would put a 403 behind a menu item that looks available. */}
                {canManage && (
                  <>
                    <Menu.Item onClick={() => onMove(node)}>{t('page.move')}</Menu.Item>
                    <Menu.Item component={Link} to={`/admin/access/${encodePath(node.path)}`}>
                      {t('admin.access')}
                    </Menu.Item>
                    <Menu.Item color="red" onClick={() => onDelete(node)}>
                      {t('nav.deleteFolder')}
                    </Menu.Item>
                  </>
                )}
              </Menu.Dropdown>
            </Menu>
          )}
          <IconChevronRight
            size={14}
            style={{ transform: opened ? 'rotate(90deg)' : undefined, transition: 'transform 120ms' }}
          />
        </Group>
      }
      opened={opened}
      onClick={() => setOpened((o) => !o)}
      childrenOffset={16}
    >
      {sortNodes(node.children, collator).map((child) => (
        <TreeItem
          key={child.path}
          node={child}
          collator={collator}
          currentPath={currentPath}
          onNavigate={onNavigate}
          onNewFolder={onNewFolder}
          onMove={onMove}
          onRename={onRename}
          onDelete={onDelete}
        />
      ))}
    </NavLink>
  );
}

/**
 * Keeps the caller on the screen they were on. Somebody who renames a page from the editor wants
 * the editor at the new path, not the reader — and the unsaved-changes guard would fire on the way.
 */
function routeFor(pathname: string, newPath: string): string {
  const prefix = pathname.match(/^\/(p|edit|history)\//)?.[1] ?? 'p';
  return `/${prefix}/${encodePath(newPath)}`;
}

/** Every page under a folder, at any depth — the folder delete is recursive. */
function countPages(node: TreeNode): number {
  return node.children.reduce((total, child) => total + (child.isFolder ? countPages(child) : 1), 0);
}

function sortNodes(nodes: TreeNode[], collator: Intl.Collator): TreeNode[] {
  return [...nodes].sort((a, b) => {
    if (a.isFolder !== b.isFolder) {
      return a.isFolder ? -1 : 1;
    }
    return collator.compare(a.isFolder ? a.name : a.title, b.isFolder ? b.name : b.title);
  });
}
