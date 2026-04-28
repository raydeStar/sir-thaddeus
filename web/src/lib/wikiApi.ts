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

export interface CreateWikiFolderInput {
  name?: string;
  parentFolderId?: string | null;
}

export interface CreateWikiPageInput {
  title?: string;
  folderId?: string | null;
  markdown?: string;
}

export interface UpdateWikiPageInput {
  markdown: string;
  expectedVersion?: number;
  source?: 'user' | 'ai' | 'restore';
  summary?: string;
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

export async function searchWiki(rootId: string | null, query: string): Promise<WikiSearchResult[]> {
  const params = new URLSearchParams({ query });
  if (rootId) params.set('rootId', rootId);
  const res = await runtimeFetch(token(), `/api/wiki/search?${params.toString()}`);
  return (await asJson<WikiSearchResponse>(res)).results;
}