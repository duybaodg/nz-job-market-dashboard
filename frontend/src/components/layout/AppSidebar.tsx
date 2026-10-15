import { Activity, BriefcaseBusiness, HeartPulse } from 'lucide-react'

type AppSidebarProps = {
  currentPath?: string
}

function resolvePath(currentPath?: string) {
  if (currentPath) return currentPath
  return typeof window === 'undefined' ? '/jobs' : window.location.pathname
}

export function AppSidebar({ currentPath }: AppSidebarProps) {
  const path = resolvePath(currentPath)
  const isJobs = path.startsWith('/jobs') || path === '/'
  const isTrends = path.startsWith('/trends')
  const isHealth = path.startsWith('/health')

  const navItems = [
    { label: 'Find jobs', icon: BriefcaseBusiness, href: '/jobs', active: isJobs },
    { label: 'Market trends', icon: Activity, href: '/trends', active: isTrends },
    { label: 'Crawler health', icon: HeartPulse, href: '/health', active: isHealth },
  ]

  return (
    <aside className="sidebar">
      <a className="brand" href="/jobs">
        <span className="brand-mark">
          <BriefcaseBusiness aria-hidden="true" />
        </span>
        <span>NZ Jobs Intel</span>
      </a>
      <nav aria-label="Dashboard views">
        {navItems.map((item) => (
          <a key={item.label} className={item.active ? 'active' : ''} href={item.href} aria-current={item.active ? 'page' : undefined}>
            <item.icon aria-hidden="true" />
            {item.label}
          </a>
        ))}
      </nav>
      <div className="sidebar-note">
        <span className="live-dot" aria-hidden="true" />
        <div>
          <strong>Tracked NZ tech listings</strong>
          <small>Daily source refresh</small>
          <a href="/health">View crawler health</a>
        </div>
      </div>
    </aside>
  )
}
