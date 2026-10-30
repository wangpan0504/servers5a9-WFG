# 逐方法审核的单组白名单；不是按名称、Passed或无PVF批量放行。
function Get-DevelopmentRuleProfile([string]$Group) {
    switch -CaseSensitive ($Group) {
        'chat-rate-rules' { return @{Name='chat-rate-rules-v1';Assertions=39;TimePolicy=@{Kind='fixed-profile';Value='FakeClock timestamp=0 frequency=1000 explicit-ms-advance'}} }
        'equipment-power-rules' { return @{Name='equipment-power-rules-v1';Assertions=49;TimePolicy=@{Kind='fixed-profile';Value='synthetic constants; no clock or random; ordinal decimal calculation'}} }
        # 守卫只限制执行，不等待计时器、不吞取消，不声称覆盖真实网络时序。
        'frame-crc-rules' { return @{Name='frame-crc-rules-v1';Assertions=574;TimePolicy=@{Kind='deterministic-with-watchdog';Value='Random(20261007) ordered NextBytes pinned runtime; memory fragments=1,3,11; CancellationToken.None; 90s per-read watchdog, not oracle'}} }
        default { return $null }
    }
}
