import { useEffect, useState } from 'react'

const LOCATION_EVENT = 'app:locationchange'

function currentLocation() {
  return `${window.location.pathname}${window.location.search}`
}

/** Push or replace a same-origin URL and notify the mounted app. */
export function navigate(to: string, options: { replace?: boolean } = {}) {
  if (options.replace) {
    window.history.replaceState(null, '', to)
  } else {
    window.history.pushState(null, '', to)
  }

  window.dispatchEvent(new Event(LOCATION_EVENT))
}

function normalizePathname(pathname: string) {
  const trimmed = pathname.replace(/\/+$/, '')
  return trimmed.length === 0 ? '/' : trimmed
}

/**
 * Minimal pathname + query-string router. It reuses the existing pattern of
 * switching views on `window.location` but also re-renders on Back/Forward and
 * on programmatic navigation, so URL state stays authoritative.
 */
export function useAppLocation() {
  const [location, setLocation] = useState(currentLocation)

  useEffect(() => {
    const update = () => setLocation(currentLocation())
    window.addEventListener('popstate', update)
    window.addEventListener(LOCATION_EVENT, update)
    return () => {
      window.removeEventListener('popstate', update)
      window.removeEventListener(LOCATION_EVENT, update)
    }
  }, [])

  const url = new URL(location, window.location.origin)

  return {
    pathname: normalizePathname(url.pathname),
    search: url.search,
  }
}
