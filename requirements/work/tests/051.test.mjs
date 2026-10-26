import assert from 'node:assert/strict'
import test from 'node:test'
import { withExecutablePlugin, acceptAuthorityRoot, openIncumbency, lifecycleWorkView } from '../../verification-system/tests/support/plugin-fixture.mjs'
import { userQuestionObservation } from '../../../dist/Interaction/Dispatch/DispatchSurface.js'

const program = body => 'class Js extends JsProgram { async run() { ' + body + ' } }'
const call = (session, id) => ({ sessionID: session, agent: 'manager', callID: id, messageID: 'run-' + id })
const input = async (hooks, runtime, session, id, text) => {
  const message = { id, role:'user', agent:'manager', sessionID:session,
    model:{ providerID:'fixture', modelID:'manager-model' } }
  runtime.pushHostMessage(session,{ info:{ ...message, time:{ created:20 } },
    parts:[{type:'text',text}] })
  await hooks['chat.message']({sessionID:session,agent:'manager',messageID:id},
    {message,parts:[{type:'text',text}]})
}
const completed = async (hooks,session,id) => hooks.event({event:{
  type:'message.part.updated',properties:{
    sessionID:session,part:{type:'tool',tool:'todowrite-for-manager',
      callID:id,messageID:'run-'+id,state:{status:'completed',input:{}}}
  }
}})

test('WHAT[work-051] first S4 Manager empty review asks the user once, without writing Reviewed or closing authority',async()=>{
  await withExecutablePlugin(async (hooks,_dir,_ids,runtime)=>{
    const session='manager-first-empty-051'
    await acceptAuthorityRoot(runtime,session,'manager','user-root')
    await input(hooks,runtime,session,'user-root','Please proceed.')
    await openIncumbency(runtime,session)
    const notice=[]
    runtime.client.tui={showToast:async ({body})=>{notice.push(body);return {}}}
    const output=await hooks.tool['todowrite-for-manager'].execute(
      {name:'initial-empty',program:program('return await this.review([]);'),
        intent:'Record the real ask receipt; do not fabricate a complete review.'},
      call(session,'first-empty'))
    assert.match(String(output),/WaitingForUser|awaitingUser|询问|waiting/)
    assert.equal(notice.length,1,'the Host presents the actual question to the human')
    const asked=userQuestionObservation(runtime.journal,session)
    assert.equal(asked.length,1,'one durable UserQuestionAsked is recorded')
    const work=await lifecycleWorkView(runtime,session)
    assert.ok(!work.reviewed,'no fake Reviewed while human question is pending')
    const was=runtime.abortedIds.length
    await completed(hooks,session,'first-empty')
    assert.equal(runtime.abortedIds.length,was+1,'only the physical completed tool call interrupts the attempt')
    await completed(hooks,session,'first-empty')
    assert.equal(runtime.abortedIds.length,was+1,'duplicate completed event cannot interrupt twice')
    await input(hooks,runtime,session,'reply-051','Finish exactly the user-specified mission.')
    const done=await hooks.tool['todowrite-for-manager'].execute(
      {name:'review-after-answer',program:program('return await this.review([]);'),
        intent:'Review after the same logical run received the human answer.'},
      call(session,'review-after-answer'))
    assert.match(String(done),/Promoted|firstReview|outcome|review/)
    assert.equal(notice.length,1,'answer does not cause a second synthetic question')
    const after=await lifecycleWorkView(runtime,session)
    assert.ok(after.reviewed,'real answer unlocks ordinary Manager assessment')
  })
})
