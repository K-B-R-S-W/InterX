# Real-time Android LogCat for WebSocket debugging
# Shows only WebSocketManager and MainActivity logs

Write-Host "🔍 Starting Android LogCat monitoring..." -ForegroundColor Cyan
Write-Host "📱 Filter: WebSocketManager, MainActivity" -ForegroundColor Yellow
Write-Host "⏹️  Press Ctrl+C to stop" -ForegroundColor Green
Write-Host ""

# Clear previous logs and start monitoring
adb logcat -c
adb logcat -s WebSocketManager:* MainActivity:*
