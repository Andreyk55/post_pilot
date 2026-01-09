import { useState } from 'react'
import type { ScheduledPost } from './types/post'
import { SchedulePost } from './components/SchedulePost'
import { ScheduledPosts } from './components/ScheduledPosts'
import './App.css'

function App() {
  const [posts, setPosts] = useState<ScheduledPost[]>([])

  const handleSchedule = (post: ScheduledPost) => {
    setPosts(prev => [post, ...prev])
  }

  const handleDelete = (id: string) => {
    setPosts(prev => prev.filter(post => post.id !== id))
  }

  return (
    <div className="app">
      <header className="header">
        <div className="logo-container">
          <div className="logo-icon">
            <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
              <path d="M12 2L2 7L12 12L22 7L12 2Z" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
              <path d="M2 17L12 22L22 17" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
              <path d="M2 12L12 17L22 12" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"/>
            </svg>
          </div>
          <div className="logo-text">
            <h1>Post Pilot</h1>
            <p className="tagline">Schedule your social media, effortlessly</p>
          </div>
        </div>
      </header>

      <main className="main-content">
        <div className="content-grid">
          <SchedulePost onSchedule={handleSchedule} />
          <ScheduledPosts posts={posts} onDelete={handleDelete} />
        </div>
      </main>
    </div>
  )
}

export default App