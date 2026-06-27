import './HomePage.css'

interface HomePageProps {
  onNavigate: (page: string) => void
}

const mockPosts = [
  {
    platform: 'Instagram',
    platformColor: '#e1306c',
    platformInitial: 'IG',
    text: 'Exciting new product launch! 🚀 Check out what we\'ve been working on.',
    time: 'Today · 10:00 AM',
    type: 'PHOTO',
    status: 'SCHEDULED',
    statusColor: '#f59e0b',
  },
  {
    platform: 'Facebook',
    platformColor: '#1877f2',
    platformInitial: 'f',
    text: 'Behind the scenes from our team meeting this week. 💪',
    time: 'Tomorrow · 2:30 PM',
    type: 'POST',
    status: 'DRAFT',
    statusColor: '#6b7280',
  },
  {
    platform: 'Instagram',
    platformColor: '#e1306c',
    platformInitial: 'IG',
    text: 'Weekend vibes 🌅 Don\'t forget to relax and recharge.',
    time: 'Sun · 9:00 AM',
    type: 'PHOTO',
    status: 'SCHEDULED',
    statusColor: '#f59e0b',
  },
]

export function HomePage({ onNavigate }: HomePageProps) {
  return (
    <div className="home-page">
      {/* Hero */}
      <section className="home-hero">
        <div className="home-hero__orb home-hero__orb--1" />
        <div className="home-hero__orb home-hero__orb--2" />
        <div className="home-hero__orb home-hero__orb--3" />

        <div className="home-hero__content">
          <div className="home-hero__badge">
            <span className="home-hero__badge-dot" />
            Social Media Automation
          </div>
          <h1 className="home-hero__title">
            Your posts,<br />
            <span className="home-hero__title--gradient">on autopilot.</span>
          </h1>
          <p className="home-hero__subtitle">
            Schedule content across platforms, manage connected accounts, and let AI craft the perfect caption — all from one sleek dashboard.
          </p>
          <div className="home-hero__actions">
            <button className="home-btn home-btn--primary" onClick={() => onNavigate('schedule')}>
              Schedule a Post
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <line x1="5" y1="12" x2="19" y2="12" />
                <polyline points="12 5 19 12 12 19" />
              </svg>
            </button>
            <button className="home-btn home-btn--ghost" onClick={() => onNavigate('posts')}>
              View My Posts
            </button>
          </div>
        </div>

        {/* Mock post preview card */}
        <div className="home-hero__preview">
          <div className="home-preview-card">
            <div className="home-preview-card__header">
              <div className="home-preview-card__title">Upcoming Posts</div>
              <div className="home-preview-card__count">3</div>
            </div>
            <div className="home-preview-card__list">
              {mockPosts.map((post, i) => (
                <div className="home-preview-item" key={i}>
                  <div
                    className="home-preview-item__platform"
                    style={{ background: post.platformColor }}
                  >
                    {post.platformInitial}
                  </div>
                  <div className="home-preview-item__body">
                    <p className="home-preview-item__text">{post.text}</p>
                    <div className="home-preview-item__meta">
                      <span className="home-preview-item__time">{post.time}</span>
                      <span className="home-preview-item__type">{post.type}</span>
                      <span
                        className="home-preview-item__status"
                        style={{ color: post.statusColor }}
                      >
                        ● {post.status}
                      </span>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          </div>

        </div>
      </section>

      {/* Platform logos strip */}
      <section className="home-platforms">
        <p className="home-platforms__label">Publish to your favourite platforms</p>
        <div className="home-platforms__strip">
          <div className="home-platforms__logo home-platforms__logo--fb">
            <svg viewBox="0 0 24 24" fill="currentColor"><path d="M18 2h-3a5 5 0 0 0-5 5v3H7v4h3v8h4v-8h3l1-4h-4V7a1 1 0 0 1 1-1h3z" /></svg>
            Facebook
          </div>
          <div className="home-platforms__logo home-platforms__logo--ig">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <rect x="2" y="2" width="20" height="20" rx="5" ry="5" />
              <circle cx="12" cy="12" r="4" />
              <circle cx="17.5" cy="6.5" r="0.5" fill="currentColor" />
            </svg>
            Instagram
          </div>

        </div>
      </section>
    </div>
  )
}
