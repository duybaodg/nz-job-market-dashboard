import { Activity, BarChart3, BriefcaseBusiness } from 'lucide-react'

const navItems = [
  { label: 'Overview', icon: BarChart3, href: '#overview' },
  { label: 'Market signals', icon: Activity, href: '#market-signals' },
  { label: 'Latest jobs', icon: BriefcaseBusiness, href: '#jobs' },
]

export function AppSidebar() {
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
          <a key={item.label} className={item.href === '#overview' ? 'active' : ''} href={item.href}>
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
        </div>
      </div>
    </aside>
  )
}
