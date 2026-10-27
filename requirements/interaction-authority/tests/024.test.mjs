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
