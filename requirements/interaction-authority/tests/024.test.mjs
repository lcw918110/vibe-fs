import test from 'node:test'
import assert from 'node:assert/strict'
import {
  classifyOutcome, needsInteractionRepair, repairDefectDecision,
} from '../../../dist/Interaction/Repair/CompletedTurnSurface.js'

const prose = value => [{type:'text', text:value}]
const thinking = value => [{type:'reasoning',text:value}]
const call = [{type:'tool-call',callID:'call-24',tool:'read',args:'{}'}]

test('WHAT[interaction-authority-024] every role rejects textless tool-free stop and must be repairable', () => {
  const roles=['manager','plan','orchestrator','engineer','devops','coder',
    'inspector','browser','inquiry','advisor','distiller','']
  for(const role of roles) {
    for(const parts of [[],prose(' \t\n '),thinking('private reasoning only')]) {
      const outcome=classifyOutcome(true,'stop',null,parts)
      assert.equal(outcome.kind,'TurnNeedsContinuation',role+': no formal prose or tool call cannot terminate')
      assert.equal(needsInteractionRepair(role,true,'stop',parts),true,
        role+': empty terminal must be repairable without role exemption')
    }
    assert.equal(classifyOutcome(true,'stop',null,prose('actual answer')).kind,'TurnCompleted')
    assert.equal(classifyOutcome(true,'stop',null,call).kind,'TurnCompleted',
      'an actual tool call is not a textless tool-free termination')
  }
})

test('WHAT[interaction-authority-024] every NEW empty repair terminal is another nudge occasion with no maximum', () => {
  for(let number=0;number<100;number++) {
    assert.equal(repairDefectDecision(true,true,'stop',[]),'RequestRepair',
      'repair must not exhaust after '+number+' actual empty terminations')
    assert.equal(repairDefectDecision(true,true,'stop',thinking('nothing final')),'RequestRepair')
  }
  assert.equal(repairDefectDecision(true,true,'stop',prose('completed')),'NoRepair')
})

// 现场 2026-10-28 10022 Manager ses_e7ed7cdb2ffe…：Engineer 父方恢复（wg46yn）
// msg_1817dc764… 07:51:06 finish=stop，只有 step-start/step-finish，无正文无工具。
// 旧判定把 step 标记当成工具调用 → TurnCompleted → "completed with empty terminal output"
// 失败终态，父方 join 拿到「父方恢复 未能完成此项任务」而不是 nudge。
const step = type => ({type})
test('WHAT[interaction-authority-024] OpenCode step markers are not a tool call: an empty stop shell still earns the nudge', () => {
  const shells=[[step('step-start'),step('step-finish')],
    [step('step-start'),{type:'reasoning',text:'only thinking'},step('step-finish')],
    [step('step-start'),{type:'text',text:'  \n'},step('step-finish')]]
  for(const role of ['engineer','devops','advisor','manager','']) {
    for(const parts of shells) {
      assert.equal(classifyOutcome(true,'stop',null,parts).kind,'TurnNeedsContinuation',
        role+': a step-wrapped empty stop is no closing report')
      assert.equal(needsInteractionRepair(role,true,'stop',parts),true,role+': empty step shell must be nudged')
      assert.equal(repairDefectDecision(true,true,'stop',parts),'RequestRepair')
    }
  }
  const settledTool={type:'tool',callID:'c24',tool:'read',state:{status:'completed',output:'ok'}}
  assert.equal(classifyOutcome(true,'stop',null,[step('step-start'),settledTool,step('step-finish')]).kind,'TurnCompleted',
    'a settled OpenCode tool part is a real tool call')
  assert.equal(classifyOutcome(true,'stop',null,[step('step-start'),{type:'text',text:'报告'},step('step-finish')]).kind,'TurnCompleted')
  assert.equal(classifyOutcome(true,'tool-calls',null,[step('step-start'),step('step-finish')]).kind,'TurnInProgress')
})
