import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import './index.css'
import App from './App.tsx'
import { HealthPage } from './pages/HealthPage.tsx'

const queryClient = new QueryClient()

const path = window.location.pathname.replace(/\/+$/, '')
const isHealthPage = path === '/health'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      {isHealthPage ? <HealthPage /> : <App />}
    </QueryClientProvider>
  </StrictMode>,
)
