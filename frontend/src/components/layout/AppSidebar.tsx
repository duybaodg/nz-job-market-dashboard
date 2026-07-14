import { BarChart3, BriefcaseBusiness, Building2, GraduationCap, Lightbulb, Map, WalletCards } from 'lucide-react'

const navItems = [
  { label: 'Overview', icon: BarChart3, active: true },
  { label: 'Regions', icon: Map },
  { label: 'Skills', icon: Lightbulb },
  { label: 'Salary', icon: WalletCards },
  { label: 'Companies', icon: Building2 },
  { label: 'Graduate', icon: GraduationCap },
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
      <nav>
        {navItems.map((item) => (
          <button key={item.label} className={item.active ? 'active' : ''} type="button">
            <item.icon aria-hidden="true" />
            {item.label}
          </button>
        ))}
      </nav>
    </aside>
  )
}
