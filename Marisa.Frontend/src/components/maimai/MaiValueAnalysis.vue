<template>
    <MaiCardShell v-if="data" class="value-analysis" :bg-key="bgKey" :accent="accent" :width="1180" pad-bottom="pb-8">
        <header>
            <div>
                <div class="eyebrow">MAIMAI DX · VALUE ANALYSIS</div>
                <div class="mt-2 flex items-center gap-4">
                    <h1>{{ title }}</h1>
                    <span class="scope-chip">{{ data.Scope }}</span>
                </div>
            </div>
        </header>

        <section class="overview mt-6">
            <div class="player-panel">
                <div class="player-label">PLAYER</div>
                <div class="nickname">{{ data.Nickname }}</div>
                <div class="rating-row">
                    <span>DX RATING</span>
                    <strong>{{ data.PlayerRating }}</strong>
                </div>
                <div class="source-row">
                    <span class="source source-curve">CURVE {{ data.CurveCount }}</span>
                    <span class="source source-df">DF {{ data.DivingFishCount }}</span>
                    <span v-if="data.MissingCount" class="source source-missing">缺失 {{ data.MissingCount }}</span>
                </div>
            </div>

            <div class="stats-grid">
                <div class="stat-card">
                    <span>可计算谱面</span>
                    <strong>{{ data.AnalyzedCount }}<small>/{{ data.SelectedCount }}</small></strong>
                </div>
                <div class="stat-card featured">
                    <span>平均偏差</span>
                    <strong :style="{ color: deviationColor(stats.Mean) }">{{ signed(stats.Mean) }}</strong>
                </div>
                <div class="stat-card wide">
                    <span>平均偏差 95% CI</span>
                    <strong>{{ signed(stats.MeanCiLow) }} ～ {{ signed(stats.MeanCiHigh) }}</strong>
                </div>
                <div class="stat-card">
                    <span>中位数</span>
                    <strong>{{ signed(stats.Median) }}</strong>
                </div>
                <div class="stat-card">
                    <span>标准差</span>
                    <strong>{{ stats.StandardDeviation.toFixed(3) }}</strong>
                </div>
                <div class="stat-card wide">
                    <span>偏差范围</span>
                    <strong>{{ signed(stats.Minimum) }} ～ {{ signed(stats.Maximum) }}</strong>
                </div>
            </div>
        </section>

        <section class="mt-7">
            <div class="section-head">
                <div class="section-title">{{ listTitle }}</div>
                <div class="section-line"></div>
                <div class="section-note">偏差 = 拟合定数 − 官方定数</div>
            </div>

            <div class="rank-columns mt-4">
                <div v-for="(column, columnIndex) in columns" :key="columnIndex" class="rank-column">
                    <article v-for="(item, rowIndex) in column" :key="`${item.SongId}-${item.LevelIndex}`" class="rank-row">
                        <div class="position">{{ columnIndex * 5 + rowIndex + 1 }}</div>
                        <img :src="coverSrcOf(item.SongId)" @error="onCoverError" alt="" class="cover">
                        <div class="song-info">
                            <div class="song-top">
                                <span class="difficulty" :style="{ color: DIFF_COLORS[Math.min(item.LevelIndex, 4)] }">
                                    {{ DIFF_ABBR[Math.min(item.LevelIndex, 4)] }}
                                </span>
                                <span class="type-badge" :class="item.Type === 'DX' ? 'type-dx' : 'type-sd'">{{ item.Type }}</span>
                                <span class="song-title">{{ item.Title }}</span>
                                <span class="source" :class="item.Source === 'curve' ? 'source-curve' : 'source-df'">
                                    {{ item.Source === 'curve' ? 'CURVE' : 'DF' }}
                                </span>
                            </div>
                            <div class="fit-row">
                                <span>{{ item.OfficialConstant.toFixed(1) }}</span>
                                <span class="arrow">→</span>
                                <span>{{ item.FittedConstant.toFixed(3) }}</span>
                                <strong :style="{ color: deviationColor(item.Deviation) }">{{ signed(item.Deviation) }}</strong>
                            </div>
                            <div class="score-row">
                                <span>{{ item.Achievement.toFixed(4) }}%</span>
                                <span>Ra {{ item.Rating }}</span>
                                <img :src="rankIcon(item.AchievementRank)" alt="" class="rank-icon">
                                <img v-if="item.Fc" :src="`/assets/maimai/pic/icon_${item.Fc}.png`" alt="" class="status-icon">
                            </div>
                        </div>
                    </article>
                </div>
            </div>
        </section>

        <footer class="mt-6 flex items-baseline justify-between gap-6">
            <span>拟合来源：MAI CURVE 优先，缺失谱面回退 DIVING-FISH CHART STATS</span>
            <strong>MARISA BOT · VALUE ANALYSIS</strong>
        </footer>
    </MaiCardShell>

    <div v-else-if="loaded" class="mai-card w-[840px] px-12 py-10 antialiased">
        <div class="text-[26px] font-bold">分析数据加载失败</div>
    </div>
</template>

<script setup lang="ts">
import {computed, nextTick, ref} from 'vue'
import axios from 'axios'
import {useRoute} from 'vue-router'
import {context_get} from '@/GlobalVars'
import {bgKeyOf, coverSrcOf, COVER_FALLBACK, DIFF_COLORS} from '@/components/maimai/utils/song_card'
import MaiCardShell from '@/components/maimai/MaiCardShell.vue'

interface AnalysisStatistics {
    Mean: number
    MeanCiLow: number
    MeanCiHigh: number
    Median: number
    StandardDeviation: number
    Minimum: number
    Maximum: number
}

interface AnalysisItem {
    SongId: number
    Title: string
    Type: string
    LevelIndex: number
    Level: string
    Achievement: number
    AchievementRank: string
    Rating: number
    DxScore: number
    Fc: string
    Fs: string
    OfficialConstant: number
    FittedConstant: number
    Deviation: number
    Source: 'curve' | 'divingFish'
}

interface AnalysisData {
    Mode: 'gold' | 'water'
    Nickname: string
    PlayerRating: number
    Scope: string
    SelectedCount: number
    AnalyzedCount: number
    CurveCount: number
    DivingFishCount: number
    MissingCount: number
    Statistics: AnalysisStatistics
    TopCharts: AnalysisItem[]
}

const route = useRoute()
const data = ref<AnalysisData | null>(null)
const loaded = ref(false)

axios.get(context_get, {params: {id: route.query.id, name: 'analysis'}})
    .then(async response => {
        const payload = (typeof response.data === 'string' ? JSON.parse(response.data) : response.data) as AnalysisData
        data.value = payload
        await nextTick()
        await preload(payload)
        await fetch(`/index.html?render-ready=${Date.now()}`, {cache: 'no-store'})
    })
    .finally(() => { loaded.value = true })

const stats = computed(() => data.value!.Statistics)
const isGold = computed(() => data.value?.Mode === 'gold')
const title = computed(() => isGold.value ? '含金量分析' : '水分分析')
const listTitle = computed(() => isGold.value ? '含金谱面 TOP 10' : '水分谱面 TOP 10')
const accent = computed(() => isGold.value ? '#f5c451' : '#5ed4ff')
const bgKey = bgKeyOf(3, false)
const columns = computed(() => [data.value!.TopCharts.slice(0, 5), data.value!.TopCharts.slice(5, 10)])
const DIFF_ABBR = ['BSC', 'ADV', 'EXP', 'MAS', 'ReM']

function signed(value: number): string {
    if (Math.abs(value) < 0.0005) return '±0.000'
    return `${value > 0 ? '+' : ''}${value.toFixed(3)}`
}

function deviationColor(value: number): string {
    if (Math.abs(value) < 0.0005) return '#d9dce8'
    return value > 0 ? '#f5c451' : '#5ed4ff'
}

function rankIcon(rank: string): string {
    return `/assets/maimai/pic/rank_${rank.toLowerCase().replace('+', 'p')}.png`
}

function onCoverError(event: Event) {
    const image = event.target as HTMLImageElement
    if (!image.src.endsWith(COVER_FALLBACK)) image.src = COVER_FALLBACK
}

async function preload(payload: AnalysisData) {
    const assets = new Set<string>()
    for (const item of payload.TopCharts) {
        assets.add(coverSrcOf(item.SongId))
        assets.add(rankIcon(item.AchievementRank))
        if (item.Fc) assets.add(`/assets/maimai/pic/icon_${item.Fc}.png`)
    }

    await Promise.allSettled([
        document.fonts.load("900 17px 'SEGA NewRodin'"),
        document.fonts.load("900 38px 'Microsoft YaHei'"),
        ...Array.from(assets, source => new Promise<void>(resolve => {
            const image = new Image()
            image.onload = () => resolve()
            image.onerror = () => resolve()
            image.src = source
        })),
    ])
}
</script>

<style scoped lang="postcss" src="@/assets/css/maimai/song_card.pcss"/>

<style scoped lang="postcss">
.value-analysis { color: #fff; }
.eyebrow { font-family: 'Torus',sans-serif; font-size: 14px; font-weight: 800; letter-spacing: 0.28em; color: rgba(255,255,255,0.48); }
h1 { font-family: 'Microsoft YaHei',sans-serif; font-size: 38px; font-weight: 900; letter-spacing: 0.02em; text-shadow: 0 3px 8px rgba(0,0,0,0.45); }
.scope-chip { padding: 5px 14px; border: 1px solid rgba(255,255,255,0.2); border-radius: 9999px; background: rgba(255,255,255,0.08); font-family: 'Torus','Microsoft YaHei',sans-serif; font-size: 14px; font-weight: 800; }
.overview { display: flex; gap: 18px; }
.player-panel { width: 292px; min-height: 188px; padding: 20px 22px; border: 1px solid rgba(255,255,255,0.12); border-radius: 18px; background: rgba(5,7,18,0.48); }
.player-label { font-family: 'Torus',sans-serif; font-size: 11px; font-weight: 800; letter-spacing: 0.25em; color: rgba(255,255,255,0.42); }
.nickname { margin-top: 6px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-family: 'Microsoft YaHei','SEGA NewRodin',sans-serif; font-size: 27px; font-weight: 900; }
.rating-row { display: flex; align-items: baseline; justify-content: space-between; margin-top: 14px; color: rgba(255,255,255,0.48); font-family: 'Torus',sans-serif; font-size: 11px; font-weight: 800; letter-spacing: 0.08em; }
.rating-row strong { color: #fff; font-size: 27px; letter-spacing: 0; }
.source-row { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 14px; }
.source { display: inline-flex; align-items: center; padding: 2px 7px; border: 1px solid; border-radius: 9999px; font-family: 'Torus','Microsoft YaHei',sans-serif; font-size: 9px; font-weight: 900; letter-spacing: 0.04em; white-space: nowrap; }
.source-curve { color: #e5c8ff; border-color: rgba(218,170,255,0.42); background: rgba(140,70,200,0.18); }
.source-df { color: #8ee8ff; border-color: rgba(94,212,255,0.42); background: rgba(20,130,180,0.18); }
.source-missing { color: #ffb3b3; border-color: rgba(255,120,120,0.4); background: rgba(170,50,50,0.16); }

.stats-grid { flex: 1; display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; }
.stat-card { min-height: 88px; padding: 13px 16px; border: 1px solid rgba(255,255,255,0.1); border-radius: 14px; background: rgba(5,7,18,0.38); }
.stat-card.wide { grid-column: span 2; }
.stat-card.featured { background: rgba(255,255,255,0.07); }
.stat-card span { display: block; font-family: 'Microsoft YaHei',sans-serif; font-size: 12px; color: rgba(255,255,255,0.5); }
.stat-card strong { display: block; margin-top: 8px; font-family: 'Torus',sans-serif; font-size: 24px; line-height: 1; font-weight: 900; font-variant-numeric: tabular-nums; white-space: nowrap; }
.stat-card small { margin-left: 4px; font-size: 14px; color: rgba(255,255,255,0.4); }

.section-head { display: flex; align-items: center; gap: 14px; }
.section-title { font-family: 'Microsoft YaHei',sans-serif; font-size: 20px; font-weight: 900; white-space: nowrap; }
.section-line { height: 2px; flex: 1; border-radius: 9999px; background: rgba(255,255,255,0.14); }
.section-note { font-family: 'Microsoft YaHei',sans-serif; font-size: 11px; color: rgba(255,255,255,0.42); white-space: nowrap; }
.rank-columns { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; }
.rank-column { display: flex; flex-direction: column; gap: 9px; }
.rank-row { position: relative; display: flex; align-items: center; gap: 12px; min-height: 112px; padding: 11px 13px 11px 50px; overflow: hidden; border: 1px solid rgba(255,255,255,0.11); border-radius: 16px; background: rgba(4,6,16,0.52); }
.position { position: absolute; left: 0; top: 0; bottom: 0; width: 38px; display: flex; align-items: center; justify-content: center; background: rgba(255,255,255,0.06); font-family: 'Torus',sans-serif; font-size: 19px; font-weight: 900; color: rgba(255,255,255,0.62); }
.cover { width: 82px; height: 82px; flex: 0 0 82px; object-fit: cover; border-radius: 12px; box-shadow: 0 0 0 1px rgba(255,255,255,0.22), 0 7px 18px rgba(0,0,0,0.35); }
.song-info { min-width: 0; flex: 1; }
.song-top { display: flex; align-items: center; gap: 8px; min-width: 0; }
.difficulty { flex: 0 0 auto; font-family: 'Torus',sans-serif; font-size: 11px; font-weight: 900; }
.type-badge { flex: 0 0 auto; padding: 1px 5px; border: 1px solid; border-radius: 4px; font-family: 'Torus',sans-serif; font-size: 8px; font-weight: 900; }
.type-dx { color: #ffbd59; border-color: rgba(255,189,89,0.45); background: rgba(255,150,40,0.13); }
.type-sd { color: rgba(255,255,255,0.56); border-color: rgba(255,255,255,0.2); background: rgba(255,255,255,0.05); }
.song-title { min-width: 0; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-family: 'SEGA NewRodin','Microsoft YaHei',sans-serif; font-size: 17px; font-weight: 900; }
.fit-row { display: flex; align-items: baseline; gap: 7px; margin-top: 7px; font-family: 'Torus',sans-serif; font-size: 17px; font-weight: 800; font-variant-numeric: tabular-nums; }
.fit-row .arrow { color: rgba(255,255,255,0.35); }
.fit-row strong { margin-left: auto; font-size: 21px; }
.score-row { display: flex; align-items: center; gap: 10px; margin-top: 6px; font-family: 'Torus',sans-serif; font-size: 12px; font-weight: 700; color: rgba(255,255,255,0.56); font-variant-numeric: tabular-nums; }
.rank-icon { width: auto; height: 20px; margin-left: auto; object-fit: contain; }
.status-icon { width: auto; height: 20px; object-fit: contain; }

footer { font-family: 'Torus','Microsoft YaHei',sans-serif; font-size: 11px; font-weight: 700; color: rgba(255,255,255,0.38); }
footer strong { letter-spacing: 0.24em; white-space: nowrap; }
</style>
