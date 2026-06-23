import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useSyncExternalStore,
} from 'react';

type RawSearchValues = Record<string, unknown>;
type SearchValues = Record<string, string | undefined>;
type Params = Record<string, string>;

type RouteOptions = {
  component?: React.ComponentType;
  validateSearch?: (search: RawSearchValues) => SearchValues;
  beforeLoad?: () => void;
};

type UpdateOptions = {
  id?: string;
  path?: string;
  fullPath?: string;
  getParentRoute?: () => RouteRecord;
};

type Match = {
  route: RouteRecord;
  params: Params;
  search: SearchValues;
};

type NavigateOptions = {
  to: string;
  params?: Params;
  search?: RawSearchValues;
  replace?: boolean;
};

export type RouteRecord = {
  id: string;
  path: string;
  fullPath: string;
  component?: React.ComponentType;
  validateSearch?: (search: RawSearchValues) => SearchValues;
  beforeLoad?: () => void;
  children: RouteRecord[];
  parent?: RouteRecord;
  update: (options: UpdateOptions) => RouteRecord;
  addChildren: (children: RouteRecord[]) => RouteRecord;
  _addFileChildren: (children: Record<string, RouteRecord>) => RouteRecord;
  _addFileTypes: <_T>() => RouteRecord;
  useParams: () => Params;
  useSearch: () => SearchValues;
};

type Router = {
  routeTree: RouteRecord;
};

const RouterContext = createContext<{ matches: Match[]; index: number } | null>(null);

const navigationStore = {
  subscribe(listener: () => void) {
    window.addEventListener('popstate', listener);
    return () => window.removeEventListener('popstate', listener);
  },
  snapshot() {
    return `${window.location.pathname}${window.location.search}${window.location.hash}`;
  },
};

export function createRootRoute(options: RouteOptions): RouteRecord {
  return createRouteRecord('/', options);
}

export function createFileRoute(path: string) {
  return (options: RouteOptions): RouteRecord => createRouteRecord(path, options);
}

export function createRouter(options: { routeTree: RouteRecord }): Router {
  return { routeTree: options.routeTree };
}

export function RouterProvider({ router }: { router: Router }) {
  const locationKey = useSyncExternalStore(navigationStore.subscribe, navigationStore.snapshot);

  const matches = useMemo(() => {
    const location = new URL(locationKey, window.location.origin);
    try {
      return matchRoutes(router.routeTree, location.pathname, location.search);
    } catch (error) {
      if (isRedirect(error)) {
        replaceLocation(buildHref(error.to));
        return matchRoutes(router.routeTree, window.location.pathname, window.location.search);
      }
      throw error;
    }
  }, [locationKey, router]);

  const Root = matches[0]?.route.component;
  if (!Root) return null;

  return (
    <RouterContext.Provider value={{ matches, index: 0 }}>
      <Root />
    </RouterContext.Provider>
  );
}

export function Outlet() {
  const context = useContext(RouterContext);
  if (!context) return null;

  const nextIndex = context.index + 1;
  const match = context.matches[nextIndex];
  const Component = match?.route.component;
  if (!Component) return null;

  return (
    <RouterContext.Provider value={{ matches: context.matches, index: nextIndex }}>
      <Component />
    </RouterContext.Provider>
  );
}

type LinkProps = Omit<React.AnchorHTMLAttributes<HTMLAnchorElement>, 'href'> &
  NavigateOptions & {
    activeProps?: React.AnchorHTMLAttributes<HTMLAnchorElement>;
    activeOptions?: { exact?: boolean };
  };

export function Link({
  to,
  params,
  search,
  replace,
  activeProps,
  activeOptions,
  className,
  onClick,
  ...rest
}: LinkProps) {
  useSyncExternalStore(navigationStore.subscribe, navigationStore.snapshot);
  const href = buildHref(to, params, search);
  const isActive = activeOptions?.exact
    ? normalizePath(window.location.pathname) === normalizePath(href.split('?')[0])
    : normalizePath(window.location.pathname).startsWith(normalizePath(href.split('?')[0]));
  const activeClassName = isActive ? activeProps?.className : undefined;

  return (
    <a
      {...rest}
      href={href}
      className={[className, activeClassName].filter(Boolean).join(' ') || undefined}
      onClick={(event) => {
        onClick?.(event);
        if (event.defaultPrevented || event.button !== 0 || rest.target) return;
        event.preventDefault();
        pushLocation(href, Boolean(replace));
      }}
    />
  );
}

export function Navigate({ to, params, search, replace }: NavigateOptions) {
  useEffect(() => {
    pushLocation(buildHref(to, params, search), Boolean(replace));
  }, [params, replace, search, to]);

  return null;
}

export function useNavigate() {
  return useCallback((options: NavigateOptions) => {
    pushLocation(buildHref(options.to, options.params, options.search), Boolean(options.replace));
    return Promise.resolve();
  }, []);
}

export function redirect(options: NavigateOptions) {
  return { __redirect: true, ...options };
}

function createRouteRecord(path: string, options: RouteOptions): RouteRecord {
  const route: RouteRecord = {
    id: path,
    path,
    fullPath: path,
    component: options.component,
    validateSearch: options.validateSearch,
    beforeLoad: options.beforeLoad,
    children: [],
    update(updateOptions) {
      route.id = updateOptions.id ?? route.id;
      route.path = updateOptions.path ?? route.path;
      route.parent = updateOptions.getParentRoute?.();
      route.fullPath = updateOptions.fullPath ?? composeFullPath(route);
      return route;
    },
    addChildren(children) {
      route.children = children;
      for (const child of children) child.parent = route;
      return route;
    },
    _addFileChildren(children) {
      return route.addChildren(Object.values(children));
    },
    _addFileTypes() {
      return route;
    },
    useParams() {
      return useCurrentMatchFor(route).params;
    },
    useSearch() {
      return useCurrentMatchFor(route).search;
    },
  };

  return route;
}

function useCurrentMatchFor(route: RouteRecord): Match {
  const context = useContext(RouterContext);
  const match = context?.matches.find((candidate) => candidate.route === route);
  return match ?? { route, params: {}, search: {} };
}

function matchRoutes(root: RouteRecord, pathname: string, query: string): Match[] {
  const candidates = flattenRoutes(root)
    .filter((route) => route !== root)
    .sort((a, b) => routeScore(b.fullPath) - routeScore(a.fullPath) || routeDepth(b) - routeDepth(a));
  const search = parseSearch(query);

  for (const route of candidates) {
    const params = matchPath(route.fullPath, pathname);
    if (!params) continue;

    route.beforeLoad?.();
    const stack = routeStack(route);
    return stack.map((item) => ({
      route: item,
      params: item === root ? {} : params,
      search: item.validateSearch ? item.validateSearch(search) : search,
    }));
  }

  return [{ route: root, params: {}, search }];
}

function flattenRoutes(route: RouteRecord): RouteRecord[] {
  return [route, ...route.children.flatMap(flattenRoutes)];
}

function routeStack(route: RouteRecord): RouteRecord[] {
  const stack: RouteRecord[] = [];
  let current: RouteRecord | undefined = route;
  while (current) {
    stack.unshift(current);
    current = current.parent;
  }
  return stack;
}

function routeDepth(route: RouteRecord) {
  return routeStack(route).length;
}

function matchPath(pattern: string, pathname: string): Params | null {
  const patternParts = splitPath(pattern);
  const pathParts = splitPath(pathname);
  if (patternParts.length !== pathParts.length) return null;

  const params: Params = {};
  for (let i = 0; i < patternParts.length; i++) {
    const expected = patternParts[i];
    const actual = pathParts[i];
    if (expected.startsWith('$')) {
      params[expected.slice(1)] = decodeURIComponent(actual);
      continue;
    }
    if (expected !== actual) return null;
  }

  return params;
}

function splitPath(path: string) {
  const normalized = normalizePath(path);
  return normalized === '/' ? [] : normalized.slice(1).split('/');
}

function normalizePath(path: string) {
  if (!path || path === '/') return '/';
  return `/${path.replace(/^\/+/, '').replace(/\/+$/, '')}`;
}

function composeFullPath(route: RouteRecord) {
  if (!route.parent) return normalizePath(route.path);
  const parent = normalizePath(route.parent.fullPath);
  const child = route.path === '/' ? '' : route.path.replace(/^\/+/, '');
  return normalizePath(`${parent}/${child}`);
}

function routeScore(path: string) {
  return splitPath(path).reduce((score, part) => score + (part.startsWith('$') ? 1 : 3), 0);
}

function parseSearch(query: string): SearchValues {
  const values: SearchValues = {};
  const params = new URLSearchParams(query);
  for (const [key, value] of params) values[key] = value;
  return values;
}

function buildHref(to: string, params: Params = {}, search?: RawSearchValues) {
  let path = to;
  for (const [key, value] of Object.entries(params)) {
    path = path.replace(`$${key}`, encodeURIComponent(value));
  }

  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(search ?? {})) {
    if (value === undefined || value === null || value === '') continue;
    query.set(key, String(value));
  }

  const qs = query.toString();
  return qs ? `${path}?${qs}` : path;
}

function pushLocation(href: string, replace: boolean) {
  if (replace) {
    window.history.replaceState(null, '', href);
  } else {
    window.history.pushState(null, '', href);
  }
  window.dispatchEvent(new PopStateEvent('popstate'));
}

function replaceLocation(href: string) {
  window.history.replaceState(null, '', href);
}

function isRedirect(error: unknown): error is NavigateOptions & { __redirect: true } {
  return typeof error === 'object' && error !== null && '__redirect' in error;
}
