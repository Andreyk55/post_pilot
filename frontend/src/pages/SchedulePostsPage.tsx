import { useState } from 'react'
import type { ScheduledPost } from '../types/post'
import { SchedulePost } from '../components/SchedulePost'
import { ScheduledPosts } from '../components/ScheduledPosts'
import './SchedulePostsPage.css'

export function SchedulePostsPage() {
  const [posts, setPosts] = useState<ScheduledPost[]>([])

  const handleSchedule = (post: ScheduledPost) => {
    setPosts(prev => [post, ...prev])
  }

  const handleDelete = (id: string) => {
    setPosts(prev => prev.filter(post => post.id !== id))
  }

  return (
    <div className="schedule-posts-page">
      <h1>Schedule Posts</h1>
      <p className="page-subtitle">Plan and schedule your social media content</p>

      <div className="schedule-content">
        <SchedulePost onSchedule={handleSchedule} />
        <ScheduledPosts posts={posts} onDelete={handleDelete} />
      </div>
    </div>
  )
}