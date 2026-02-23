import { useMemo } from 'react'
import './GreetingPage.css'

function getTimeOfDay(): { label: string; icon: string } {
  const hour = new Date().getHours()
  if (hour < 12) return { label: 'Good morning', icon: '🌅' }
  if (hour < 17) return { label: 'Good afternoon', icon: '☀️' }
  return { label: 'Good evening', icon: '🌙' }
}

export function GreetingPage() {
  const { label, icon } = useMemo(getTimeOfDay, [])

  return (
    <div className="greeting-page">
      <div className="greeting-card">
        <span className="greeting-icon">{icon}</span>
        <h1 className="greeting-heading">{label}!</h1>
        <p className="greeting-subtext">
          Welcome back to Post Pilot. Ready to manage your social media today?
        </p>
        <div className="greeting-tips">
          <h2>Quick tips</h2>
          <ul>
            <li>Schedule posts in advance to stay consistent</li>
            <li>Connect your social accounts under Connected Accounts</li>
            <li>Track your post history in My Posts</li>
          </ul>
        </div>
      </div>
    </div>
  )
}
