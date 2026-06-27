import './HomePage.css'

interface HomePageProps {
  onNavigate: (page: string) => void
}

const features = [
  {
    icon: (
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <rect x="3" y="4" width="18" height="18" rx="2" ry="2" />
        <line x1="16" y1="2" x2="16" y2="6" />
        <line x1="8" y1="2" x2="8" y2="6" />
        <line x1="3" y1="10" x2="21" y2="10" />
        <path d="M8 14h.01M12 14h.01M16 14h.01M8 18h.01M12 18h.01" />
      </svg>
    ),
    label: 'Schedule Posts',
    description: 'Plan your content calendar and schedule posts across platforms in advance.',
    page: 'schedule',
    color: '#667eea',
  },
  {
    icon: (
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <path d="M17 21v-2a4 4 0 0 0-4-4H5a4 4 0 0 0-4 4v2" />
        <circle cx="9" cy="7" r="4" />
        <path d="M23 21v-2a4 4 0 0 0-3-3.87" />
        <path d="M16 3.13a4 4 0 0 1 0 7.75" />
      </svg>
    ),
    label: 'Connected Accounts',
    description: 'Link your Facebook, Instagram, and more — manage them all in one place.',
    page: 'accounts',
    color: '#f093fb',
  },
  {
    icon: (
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <rect x="3" y="3" width="18" height="18" rx="2" />
        <path d="M3 9h18M9 21V9" />
      </svg>
    ),
    label: 'Assets Library',
    description: 'Store, organise and reuse your photos and media across all your posts.',
    page: 'assets',
    color: '#4facfe',
  },
  {
    icon: (
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <path d="M12 2a10 10 0 0 1 10 10c0 5.52-4.48 10-10 10S2 17.52 2 12 6.48 2 12 2z" />
        <path d="M8 12s1.5 2 4 2 4-2 4-2" />
        <line x1="9" y1="9" x2="9.01" y2="9" />
        <line x1="15" y1="9" x2="15.01" y2="9" />
      </svg>
    ),
    label: 'AI Assist',
    description: 'Generate captions, translate content, and enhance media with AI.',
    page: 'schedule',
    color: '#43e97b',
  },
]

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

          {/* Floating stat pills */}
          <div className="home-stat home-stat--tl">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <polyline points="22 12 18 12 15 21 9 3 6 12 2 12" />
            </svg>
            <span>Reach up <strong>+42%</strong></span>
          </div>
          <div className="home-stat home-stat--br">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <circle cx="12" cy="12" r="10" />
              <polyline points="12 6 12 12 16 14" />
            </svg>
            <span>Saves <strong>4h/week</strong></span>
          </div>
        </div>
      </section>

      {/* Features */}
      <section className="home-features">
        <h2 className="home-features__title">Everything you need</h2>
        <p className="home-features__sub">Powerful tools to grow your social presence without the grind.</p>
        <div className="home-features__grid">
          {features.map(f => (
            <button
              key={f.label}
              className="home-feature-card"
              onClick={() => onNavigate(f.page)}
              style={{ '--feature-color': f.color } as React.CSSProperties}
            >
              <div className="home-feature-card__icon">{f.icon}</div>
              <h3 className="home-feature-card__label">{f.label}</h3>
              <p className="home-feature-card__desc">{f.description}</p>
              <span className="home-feature-card__arrow">→</span>
            </button>
          ))}
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
          <div className="home-platforms__logo home-platforms__logo--tw">
            <svg viewBox="0 0 24 24" fill="currentColor"><path d="M18.244 2.25h3.308l-7.227 8.26 8.502 11.24H16.17l-5.214-6.817L4.99 21.75H1.68l7.73-8.835L1.254 2.25H8.08l4.713 6.231zm-1.161 17.52h1.833L7.084 4.126H5.117z" /></svg>
            Twitter/X
            <span className="home-platforms__soon">Soon</span>
          </div>
          <div className="home-platforms__logo home-platforms__logo--li">
            <svg viewBox="0 0 24 24" fill="currentColor"><path d="M16 8a6 6 0 0 1 6 6v7h-4v-7a2 2 0 0 0-2-2 2 2 0 0 0-2 2v7h-4v-7a6 6 0 0 1 6-6zM2 9h4v12H2z" /><circle cx="4" cy="4" r="2" /></svg>
            LinkedIn
            <span className="home-platforms__soon">Soon</span>
          </div>
        </div>
      </section>
    </div>
  )
}
