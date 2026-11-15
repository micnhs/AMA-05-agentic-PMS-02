import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App'
import './style.css'

document.documentElement.dataset.theme = new URLSearchParams(location.search).get('clawpilotTheme') ?? 'light'
ReactDOM.createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>)
