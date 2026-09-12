<script setup lang="ts">
import axios from 'axios'
import {computed, nextTick, onMounted, ref, watch} from 'vue'
import {useRoute} from 'vue-router'
import {context_get, chunithm_levelColors} from '@/GlobalVars'

type Beatmap = { LevelName: string; LevelStr: string; Constant: number; Charter: string; MaxCombo: number; Bpm?: string }
type Song = { Id: number; Title: string; Artist: string; Genre: string; Version: string; Beatmaps: Beatmap[] }

const route = useRoute()
const song = ref<Song | null>(null)
const loaded = ref(false)
axios.get(context_get, {params: {id: route.query.id, name: 'SongData'}})
    .then(response => { song.value = response.data })
    .finally(() => { loaded.value = true })

const levelColors: Record<string, string> = {
    BASIC: chunithm_levelColors[0], ADVANCED: chunithm_levelColors[1],
    EXPERT: chunithm_levelColors[2], MASTER: chunithm_levelColors[3],
    ULTIMA: '#ff3a3a', "WORLD'S END": '#dbaaff',
}
const levelLights: Record<string, string> = {
    BASIC: '#70f13f', ADVANCED: '#ffbc1f', EXPERT: '#ff6e7a',
    MASTER: '#da63f8', ULTIMA: '#161616', "WORLD'S END": '#c8a0f0',
}
const versionLogos: Record<string, string> = {
    CHUNITHM: 'logo_chunithm.png', 'CHUNITHM PLUS': 'logo_chunithm_plus.png',
    'CHUNITHM AIR': 'logo_air.png', 'CHUNITHM AIR PLUS': 'logo_air_plus.png',
    'CHUNITHM STAR': 'logo_star.png', 'CHUNITHM STAR PLUS': 'logo_star_plus.png',
    'CHUNITHM AMAZON': 'logo_amazon.png', 'CHUNITHM AMAZON PLUS': 'logo_amazon_plus.png',
    'CHUNITHM CRYSTAL': 'logo_crystal.png', 'CHUNITHM CRYSTAL PLUS': 'logo_crystal_plus.png',
    'CHUNITHM PARADISE': 'logo_paradise.png', 'CHUNITHM PARADISE LOST': 'logo_paradise_lost.png',
    'CHUNITHM NEW!!': 'logo_new.png', 'CHUNITHM NEW PLUS!!': 'logo_new_plus.png',
    'CHUNITHM SUN': 'logo_sun.png', 'CHUNITHM SUN PLUS': 'logo_sun_plus.png',
    'CHUNITHM LUMINOUS': 'logo_luminous.png', 'CHUNITHM LUMINOUS PLUS': 'logo_luminous_plus.png',
    'CHUNITHM VERSE': 'logo_verse.png', 'CHUNITHM XVERSE': 'logo_xverse.png',
    'CHUNITHM XVERSEX': 'logo_xversex.png',
}

const beatmaps = computed(() => song.value?.Beatmaps ?? [])
const chartCount = computed(() => beatmaps.value.length)
const hasWorldsEnd = computed(() => beatmaps.value.some(chart => chart.LevelName === "WORLD'S END"))
const themeColor = computed(() => {
    for (let i = beatmaps.value.length - 1; i >= 0; i--) {
        const chart = beatmaps.value[i]
        if (chart.Constant > 0) return levelColors[chart.LevelName] ?? '#fdd500'
    }
    return '#fdd500'
})
const versionLogo = computed(() => {
    const version = song.value?.Version ?? ''
    const key = Object.keys(versionLogos).sort((a, b) => b.length - a.length)
        .find(item => version === item || version.includes(item))
    return key ? versionLogos[key] : ''
})
const bpmText = computed(() => {
    const values = beatmaps.value.flatMap(chart => (chart.Bpm ?? '').match(/[\d.]+/g)?.map(Number) ?? [])
        .filter(value => Number.isFinite(value) && value > 0)
    const unique = [...new Set(values)].sort((a, b) => a - b)
    if (!unique.length) return ''
    return unique.length === 1 ? String(unique[0]) : `${unique[0]}–${unique[unique.length - 1]}`
})

function levelColor(chart: Beatmap) { return levelColors[chart.LevelName] ?? '#888' }
function levelLight(chart: Beatmap) { return levelLights[chart.LevelName] ?? '#555' }
function displayLevel(chart: Beatmap) { return chart.Constant > 0 ? chart.Constant.toFixed(1) : chart.LevelStr }
function charter(chart: Beatmap) { return chart.Charter?.trim() || 'N/A' }
function rowStyle(chart: Beatmap) {
    const color = levelColor(chart)
    const ultima = chart.LevelName === 'ULTIMA'
    return {
        background: `linear-gradient(90deg, ${color}26 0%, rgba(0,0,0,.42) 34%, rgba(0,0,0,.42) 100%)`,
        border: ultima ? '1px solid rgba(255,58,58,.4)' : '1px solid rgba(255,255,255,.09)',
        boxShadow: ultima ? 'inset 4px 0 0 #ff3a3a' : `inset 4px 0 0 ${color}`,
    }
}
function coverError(event: Event) {
    const image = event.target as HTMLImageElement
    if (!image.src.endsWith('/0.png')) image.src = '/assets/chunithm/cover/0.png'
}
function deduction(combo: number) {
    if (!Number.isFinite(combo) || combo <= 0) return null
    const justice = 10000 / combo
    return {miss: (justice * 101).toFixed(1), attack: (justice * 51).toFixed(1), justice: justice.toFixed(1)}
}
function tolerance(combo: number) {
    if (!Number.isFinite(combo) || combo <= 0) return null
    const cells = (buffer: number) => {
        const total = Math.floor(combo * buffer)
        const attack = Math.floor(total / 51)
        return {attack, justice: total - attack * 51}
    }
    return {sss: cells(.25), sssp: cells(.1)}
}
const masterTolerance = computed(() => {
    const chart = beatmaps.value.find(item => item.LevelName === 'MASTER')
    return chart ? tolerance(chart.MaxCombo) : null
})
const ultimaTolerance = computed(() => {
    const chart = beatmaps.value.find(item => item.LevelName === 'ULTIMA')
    return chart ? tolerance(chart.MaxCombo) : null
})

const titleEl = ref<HTMLElement | null>(null)
const titleSize = ref(80)
async function fitTitle() {
    await nextTick()
    const element = titleEl.value
    if (!element) return
    titleSize.value = 80
    await nextTick()
    try { await document.fonts.ready } catch {}
    await nextTick()
    for (let pass = 0; pass < 4 && element.scrollWidth > element.clientWidth; pass++) {
        titleSize.value = Math.max(28, Math.floor(titleSize.value * element.clientWidth / element.scrollWidth))
        await nextTick()
    }
}
watch(() => song.value?.Title, () => { setTimeout(() => { void fitTitle() }, 0) }, {flush: 'post'})
onMounted(() => { void fitTitle() })
</script>

<template>
    <div v-if="!loaded" class="chu-song-loading">CHUNITHM SONG {{ route.query.id }}</div>
    <div v-else-if="song" class="chu-song" :style="{'--theme': themeColor}">
        <div class="base-layer"></div><div class="stripe-layer"></div><div class="glow-layer"></div>
        <div class="inner">
            <header class="top-bar">
                <img v-if="versionLogo" :src="`/assets/chunithm/pic/${versionLogo}`" class="version-logo" alt="">
                <span v-else class="version-tag">{{ song.Version }}</span><span class="spacer"></span>
                <span class="bpm-pill"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9.4 3h5.2c.5 0 .9.33 1 .8l3.1 14.6c.13.62-.34 1.2-1 1.2H6.3c-.66 0-1.13-.58-1-1.2L8.4 3.8c.1-.47.5-.8 1-.8z"/><path d="m12 15.2 5.6-9.6"/><circle cx="12" cy="15.6" r="1.5"/></svg>{{ bpmText }}</span>
                <span class="meta-chip">{{ song.Genre }}</span><span class="id-pill">ID {{ song.Id }}</span>
            </header>
            <h1 ref="titleEl" class="song-title" :style="{fontSize: `${titleSize}px`}">{{ song.Title }}</h1>
            <div class="artist-line">{{ song.Artist }}</div>
            <div class="two-col">
                <div class="left-col">
                    <div class="cover-frame"><img :src="`/assets/chunithm/cover/${song.Id}.png`" class="cover-img" alt="" @error="coverError"></div>
                    <div v-if="masterTolerance || ultimaTolerance" class="tolerance-section">
                        <div v-for="item in [{name:'MASTER', value:masterTolerance, cls:'master'}, {name:'ULTIMA', value:ultimaTolerance, cls:'ultima'}]" :key="item.name" v-show="item.value" class="tol-block" :class="item.cls">
                            <div class="tol-heading"><span>RANK TOLERANCE</span><strong>{{ item.name }}</strong></div>
                            <div class="tol-values"><div><img src="/assets/chunithm/pic/rank_sss.png" alt="SSS"><b>{{ item.value?.sss.attack }}</b><em>+{{ item.value?.sss.justice }}</em></div><div><img src="/assets/chunithm/pic/rank_sssp.png" alt="SSS+"><b>{{ item.value?.sssp.attack }}</b><em>+{{ item.value?.sssp.justice }}</em></div></div>
                        </div>
                    </div>
                </div>
                <section class="right-col">
                    <div class="section-head"><span class="section-tag">谱面信息</span><span class="section-label">CHART DATA</span><i></i><span class="section-count">{{ chartCount }} CHARTS</span></div>
                    <div class="chart-list">
                        <div v-for="chart in beatmaps" :key="`${chart.LevelName}-${chart.LevelStr}`" class="chart-row" :style="rowStyle(chart)">
                            <div class="chart-top"><div class="diff-chip" :class="{ultima: chart.LevelName === 'ULTIMA'}"><span class="diff-name" :style="{backgroundColor: levelColor(chart), width: hasWorldsEnd ? '220px' : '160px'}">{{ chart.LevelName }}</span><span class="diff-level" :style="{backgroundColor: levelLight(chart), color: chart.LevelName === 'ULTIMA' ? '#fff' : '#222'}">{{ chart.LevelStr }}</span></div><span class="charter" :class="{muted: !chart.Charter?.trim()}">{{ charter(chart) }}</span></div>
                            <span class="constant" :style="{borderColor: levelColor(chart), color: levelColor(chart), boxShadow: `0 0 18px ${levelColor(chart)}66`, textShadow: `0 0 10px ${levelColor(chart)}88`}">{{ displayLevel(chart) }}</span>
                            <div class="note-row"><div class="note-cell"><span>COMBO</span><b>{{ chart.MaxCombo > 0 ? chart.MaxCombo.toLocaleString() : '—' }}</b></div><template v-if="deduction(chart.MaxCombo)" v-for="item in [{label:'MISS', value:deduction(chart.MaxCombo)?.miss, class:'miss'}, {label:'ATTACK', value:deduction(chart.MaxCombo)?.attack, class:'attack'}, {label:'JUSTICE', value:deduction(chart.MaxCombo)?.justice, class:'justice'}]" :key="item.label"><div class="note-cell"><span>{{ item.label }}</span><b :class="item.class">{{ item.value }}</b></div></template></div>
                        </div>
                    </div>
                </section>
            </div>
            <footer>MARISA BOT · CHUNITHM SONG</footer>
        </div>
    </div>
</template>

<style scoped lang="postcss">
.chu-song{position:relative;width:1240px;overflow:hidden;color:#fff;background:#0e0418}.chu-song img{max-width:none}.base-layer,.stripe-layer,.glow-layer{position:absolute;inset:0;pointer-events:none}.base-layer{background:linear-gradient(168deg,color-mix(in srgb,var(--theme) 14%,transparent),#0e0418 62%,#060309)}.stripe-layer{background:repeating-linear-gradient(-38deg,rgba(255,255,255,.028) 0 3px,transparent 3px 26px)}.glow-layer{background:radial-gradient(620px 620px at 24% 30%,color-mix(in srgb,var(--theme) 20%,transparent),transparent 70%),radial-gradient(520px 420px at 88% 6%,color-mix(in srgb,var(--theme) 12%,transparent),transparent 70%)}.inner{position:relative;padding:44px 48px 32px}.top-bar{display:flex;align-items:center;gap:12px;margin:0 44px 14px;min-height:90px}.version-logo{width:auto;height:90px;filter:drop-shadow(0 2px 4px rgba(0,0,0,.3))}.version-tag,.id-pill,.bpm-pill{font-family:'Torus',sans-serif;font-weight:bold;color:#fff;background:rgba(35,37,69,.82);border-radius:9999px;box-shadow:0 4px 12px rgba(35,37,69,.25)}.version-tag{padding:6px 20px;font-size:20px;letter-spacing:.12em}.spacer{flex:1}.bpm-pill,.id-pill{display:inline-flex;align-items:center;padding:5px 18px;font-size:22px}.bpm-pill{gap:9px;padding-left:14px}.bpm-pill svg{width:20px;height:20px;fill:none;stroke:currentColor;stroke-width:1.9;stroke-linejoin:round;stroke-linecap:round}.meta-chip{padding:5px 16px;border-radius:9999px;color:#454867;background:rgba(255,255,255,.72);box-shadow:0 0 0 1px rgba(255,255,255,.85),0 3px 10px rgba(90,40,120,.15);backdrop-filter:blur(8px);font: bold 17px 'Microsoft YaHei',sans-serif}.song-title{width:1144px;margin:0 44px;padding:8px 0 10px;font:700 80px/1 'SEGA NewRodin','Microsoft YaHei',sans-serif;white-space:nowrap;overflow:hidden;color:#fff;text-shadow:0 2px 4px rgba(0,0,0,.5),0 4px 22px rgba(0,0,0,.4)}.artist-line{margin:28px 44px 26px;font: bold 23px/33px 'Torus','Microsoft YaHei',sans-serif;color:rgba(255,255,255,.82);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.two-col{display:flex;gap:28px;margin:0 44px;align-items:stretch}.left-col{width:452px;flex-shrink:0;display:flex;flex-direction:column}.cover-frame{padding:6px;border-radius:30px;background:rgba(255,255,255,.75);box-shadow:0 0 0 1px rgba(255,255,255,.8),0 8px 20px -12px rgba(0,0,0,.5)}.cover-img{display:block;width:440px;height:440px;border-radius:24px;object-fit:cover}.tolerance-section{display:flex;flex-direction:column;gap:12px;margin-top:auto}.tol-block{overflow:hidden;border-radius:12px;background:rgba(255,255,255,.05);box-shadow:inset 3px 0 0 var(--accent)}.tol-block.master{--accent:#c64fe4}.tol-block.ultima{--accent:#ff3a3a}.tol-heading{display:flex;align-items:baseline;gap:8px;padding:10px 16px 6px;font-family:'Microsoft YaHei',sans-serif}.tol-heading span{color:rgba(255,255,255,.5);font-size:15px;letter-spacing:.08em}.tol-heading strong{font-size:16px}.tol-values{display:flex;padding:4px 0 10px}.tol-values>div{display:flex;flex:1;align-items:center;justify-content:center;gap:8px}.tol-values img{width:auto;height:28px;filter:drop-shadow(0 2px 3px rgba(0,0,0,.4))}.tol-values b{color:#22c55e;font:bold 31px 'Torus',sans-serif}.tol-values em{color:#f59e0b;font:bold 24px 'Torus',sans-serif;font-style:normal}.right-col{flex:1;min-width:0}.section-head{display:flex;align-items:center;gap:12px;height:44px;margin-bottom:14px}.section-tag{padding:4px 20px;border-radius:9999px;background:#5a2878;box-shadow:0 0 0 2px rgba(255,255,255,.7),0 3px 10px rgba(90,40,120,.3);font:bold 22px 'Microsoft YaHei',sans-serif;letter-spacing:.1em;white-space:nowrap}.section-label{font:900 20px 'SEGA NewRodin','Microsoft YaHei',sans-serif;letter-spacing:.25em;color:rgba(255,255,255,.75);white-space:nowrap}.section-head i{flex:1;height:2px;border-radius:10px;background:rgba(255,255,255,.15)}.section-count{color:rgba(255,255,255,.5);font:bold 17px 'Torus',sans-serif;letter-spacing:.18em;white-space:nowrap}.chart-list{display:flex;flex-direction:column;gap:10px}.chart-row{position:relative;height:112px;overflow:hidden;border-radius:10px}.chart-top{display:flex;align-items:stretch}.diff-chip{display:inline-flex;flex-shrink:0;align-items:stretch;overflow:hidden;border-radius:10px 0 14px 0;box-shadow:0 2px 8px rgba(0,0,0,.35)}.diff-chip.ultima{border-left:4px solid #ff3a3a}.diff-name,.diff-level{display:flex;align-items:center;box-sizing:border-box;font-weight:bold}.diff-name{justify-content:flex-end;padding:0 14px 0 18px;font: bold 20px 'SEGA NewRodin','Microsoft YaHei',sans-serif;letter-spacing:.06em;white-space:nowrap}.diff-level{justify-content:center;width:64px;padding:0 18px;font:bold 22px 'Torus',sans-serif}.charter{min-width:0;margin:0 14px 0 auto;overflow:hidden;color:#fff;font:bold 18px 'SEGA NewRodin','Microsoft YaHei',sans-serif;white-space:nowrap;text-overflow:ellipsis;align-self:center}.charter.muted{color:rgba(255,255,255,.4)}.constant{position:absolute;right:12px;bottom:10px;padding:4px 14px;border:2.5px solid;border-radius:12px;background:rgba(8,8,16,.35);font:bold 34px 'Torus',sans-serif}.note-row{position:absolute;inset:44px 120px 0 14px;display:flex;align-items:flex-end;padding-bottom:12px}.note-cell{display:flex;flex:1;flex-direction:column;align-items:center;min-width:0}.note-cell span{margin-bottom:2px;color:rgba(255,255,255,.6);font:13px 'Torus',sans-serif;letter-spacing:.1em}.note-cell b{color:#fff;font:bold 22px 'Torus',sans-serif}.note-cell .miss{color:rgba(255,255,255,.5)}.note-cell .attack{color:#1afe11}.note-cell .justice{color:#e98807}footer{margin:20px 44px 0;color:rgba(255,255,255,.25);font:15px 'Torus',sans-serif;letter-spacing:.25em}.chu-song-loading{display:flex;align-items:center;justify-content:center;width:1240px;height:600px;color:rgba(255,255,255,.4);background:#0e0418;font:bold 30px 'Torus',sans-serif;letter-spacing:.15em}
</style>
