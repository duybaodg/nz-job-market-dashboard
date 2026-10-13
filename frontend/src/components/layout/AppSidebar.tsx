import { Activity, BarChart3, BriefcaseBusiness, HeartPulse } from 'lucide-react'

function currentPath() {
  return typeof window === 'undefined' ? '/' : window.location.pathname
}

export function AppSidebar() {
  const isHealth = currentPath().startsWith('/health')

  const navItems = [
    { label: 'Overview', icon: BarChart3, href: '/', active: !isHealth },
    { label: 'Market signals', icon: Activity, href: '/#market-signals', active: false },
    { label: 'Latest jobs', icon: BriefcaseBusiness, href: '/#jobs', active: false },
    { label: 'Crawler health', icon: HeartPulse, href: '/health', active: isHealth },
  ]

  return (
    <aside className="sidebar">
      <div className="brand">
        <span className="brand-mark">
          <BriefcaseBusiness aria-hidden="true" />
        </span>
        <span>NZ Jobs Intel</span>
      </div>
      <nav aria-label="Dashboard sections">
        {navItems.map((item) => (
          <a key={item.label} className={item.active ? 'active' : ''} href={item.href}>
            <item.icon aria-hidden="true" />
            {item.label}
          </a>
        ))}
      </nav>
      <div className="sidebar-note">
        <span className="live-dot" aria-hidden="true" />
        <div>
          <strong>Live data</strong>
          <small>Daily source refresh</small>
          <a href="/health">View crawler health</a>
        </div>
      </div>
    </aside>
  )
}
