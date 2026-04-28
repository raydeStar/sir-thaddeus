import { runtimeFetch, readRuntimeMetadata, parseRuntimeJson } from './runtime';

function token(): string {
  return readRuntimeMetadata().token;
}

const asJson = parseRuntimeJson;

export interface WikiRoot {
  id: string;
  name: string;
  path: string;
  createdAt: string;
  updatedAt: string;
}

export interface WikiFolder {
  id: string;
  rootId: string;
  parentFolderId: string | null;
  name: string;
  slug: string;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}

export interface WikiPage {
  id: string;
  rootId: string;
  folderId: string | null;
  title: string;
  slug: string;
  relativePath: string;
  version: number;
  createdAt: string;
  updatedAt: string;
  excerpt: string;
  wordCount: number;
}

export interface WikiPageDocument {
  page: WikiPage;
  markdown: string;
}

export interface WikiRevision {
  id: string;
  pageId: string;
  version: number;
  source: string;
  createdAt: string;
  summary: string | null;
  markdown: string;
}

export interface WikiTree {
  root: WikiRoot;
  folders: WikiFolder[];
  pages: WikiPage[];
}

export interface WikiSearchResult {
  rootId: string;
  pageId: string;
  title: string;
  excerpt: string;
  relativePath: string;
  version: number;
}

export interface WikiPageAssistantReply {
  answer: string;
  createdAt: string;
  messageId: string;
}

export interface WikiPageDraft {
  markdown: string;
  assistantText: string;
  summary: string;
  createdAt: string;
  messageId: string;
}

export interface WikiSelectionRewriteDraft {
  selectedText: string;
  replacementText: string;
  markdown: string;
  assistantText: string;
  summary: string;
  createdAt: string;
  messageId: string;
}

interface WikiRootsResponse {
  roots: WikiRoot[];
}

interface WikiRevisionsResponse {
  revisions: WikiRevision[];
}

interface WikiSearchResponse {
  results: WikiSearchResult[];
}

export interface CreateWikiRootInput {
  name?: string;
  path?: string;
}

export interface UpdateWikiRootInput {
  name: string;
}

export interface CreateWikiFolderInput {
  name?: string;
  parentFolderId?: string | null;
}

export interface UpdateWikiFolderInput {
  name: string;
}

export interface CreateWikiPageInput {
  title?: string;
  folderId?: string | null;
  markdown?: string;
}

export interface UpdateWikiPageInput {
  markdown?: string;
  title?: string;
  expectedVersion?: number;
  source?: 'user' | 'ai' | 'restore';
  summary?: string;
}

export interface MoveWikiPageInput {
  folderId?: string | null;
  expectedVersion?: number;
}

export interface WikiPageChatInput {
  prompt: string;
  scope?: string;
}

export interface WikiPageDraftInput {
  instruction: string;
  scope?: string;
}

export interface WikiSelectionRewriteInput {
  selectedText: string;
  instruction: string;
  expectedVersion?: number;
  scope?: string;
}

export async function listWikiRoots(): Promise<WikiRoot[]> {
  const res = await runtimeFetch(token(), '/api/wiki/roots');
  return (await asJson<WikiRootsResponse>(res)).roots;
}

export async function createWikiRoot(input: CreateWikiRootInput): Promise<WikiRoot> {
  const res = await runtimeFetch(token(), '/api/wiki/roots', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<WikiRoot>(res);
}

export async function updateWikiRoot(rootId: string, input: UpdateWikiRootInput): Promise<WikiRoot> {
  const res = await runtimeFetch(token(), `/api/wiki/roots/${encodeURIComponent(rootId)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<WikiRoot>(res);
}

export async function getWikiTree(rootId: string): Promise<WikiTree> {
  const res = await runtimeFetch(token(), `/api/wiki/roots/${encodeURIComponent(rootId)}/tree`);
  return asJson<WikiTree>(res);
}

export async function createWikiFolder(rootId: string, input: CreateWikiFolderInput): Promise<WikiFolder> {
  const res = await runtimeFetch(token(), `/api/wiki/roots/${encodeURIComponent(rootId)}/folders`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<WikiFolder>(res);
}

export async function updateWikiFolder(
  rootId: string,
  folderId: string,
  input: UpdateWikiFolderInput,
): Promise<WikiFolder> {
  const res = await runtimeFetch(
    token(),
    `/api/wiki/roots/${encodeURIComponent(rootId)}/folders/${encodeURIComponent(folderId)}`,
    {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    },
  );
  return asJson<WikiFolder>(res);
}

export async function createWikiPage(rootId: string, input: CreateWikiPageInput): Promise<WikiPageDocument> {
  const res = await runtimeFetch(token(), `/api/wiki/roots/${encodeURIComponent(rootId)}/pages`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<WikiPageDocument>(res);
}

export async function getWikiPage(pageId: string): Promise<WikiPageDocument> {
  const res = await runtimeFetch(token(), `/api/wiki/pages/${encodeURIComponent(pageId)}`);
  return asJson<WikiPageDocument>(res);
}

export async function updateWikiPage(pageId: string, input: UpdateWikiPageInput): Promise<WikiPageDocument> {
  const res = await runtimeFetch(token(), `/api/wiki/pages/${encodeURIComponent(pageId)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<WikiPageDocument>(res);
}

export async function moveWikiPage(pageId: string, input: MoveWikiPageInput): Promise<WikiPageDocument> {
  const res = await runtimeFetch(token(), `/api/wiki/pages/${encodeURIComponent(pageId)}/location`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<WikiPageDocument>(res);
}

export async function listWikiRevisions(pageId: string): Promise<WikiRevision[]> {
  const res = await runtimeFetch(token(), `/api/wiki/pages/${encodeURIComponent(pageId)}/revisions`);
  return (await asJson<WikiRevisionsResponse>(res)).revisions;
}

export async function restoreWikiRevision(
  pageId: string,
  revisionId: string,
  expectedVersion?: number,
): Promise<WikiPageDocument> {
  const res = await runtimeFetch(
    token(),
    `/api/wiki/pages/${encodeURIComponent(pageId)}/revisions/${encodeURIComponent(revisionId)}/restore`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ expectedVersion }),
    },
  );
  return asJson<WikiPageDocument>(res);
}

export async function askWikiPage(pageId: string, input: WikiPageChatInput): Promise<WikiPageAssistantReply> {
  const res = await runtimeFetch(token(), `/api/wiki/pages/${encodeURIComponent(pageId)}/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<WikiPageAssistantReply>(res);
}

export async function draftWikiPage(pageId: string, input: WikiPageDraftInput): Promise<WikiPageDraft> {
  const res = await runtimeFetch(token(), `/api/wiki/pages/${encodeURIComponent(pageId)}/draft`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<WikiPageDraft>(res);
}

export async function rewriteWikiSelection(
  pageId: string,
  input: WikiSelectionRewriteInput,
): Promise<WikiSelectionRewriteDraft> {
  const res = await runtimeFetch(token(), `/api/wiki/pages/${encodeURIComponent(pageId)}/selection/rewrite`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  });
  return asJson<WikiSelectionRewriteDraft>(res);
}

export async function searchWiki(rootId: string | null, query: string): Promise<WikiSearchResult[]> {
  const params = new URLSearchParams({ query });
  if (rootId) params.set('rootId', rootId);
  const res = await runtimeFetch(token(), `/api/wiki/search?${params.toString()}`);
  return (await asJson<WikiSearchResponse>(res)).results;
}