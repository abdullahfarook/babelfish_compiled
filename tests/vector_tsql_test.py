import pymssql,sys
c=pymssql.connect('127.0.0.1',user='postgres',password='postgres',database='master',port=int(__import__('os').environ.get('TDS',1433)),autocommit=True); cu=c.cursor()
ok=0;bad=0
def t(name,q,p=None,expect=None,err=False):
    global ok,bad
    try:
        cu.execute(q,p); r=cu.fetchall() if cu.description else None
        good = (not err) and (expect is None or r==expect)
    except Exception as e:
        r=str(e)[:120]; good = err
    ok+=good; bad+=not good; print("PASS" if good else "FAIL",name,"" if good else r)
cu.execute("IF OBJECT_ID('vt') IS NOT NULL DROP TABLE vt")
t("create table","CREATE TABLE vt (id int primary key, tag nvarchar(20), v vector(3))")
t("insert","INSERT INTO vt VALUES (1,'a','[1,2,3]'),(2,'b','[4,5,6]'),(3,'c','[1,0,0]'),(4,'d','[0,1,0]')")
t("select roundtrip","SELECT v FROM vt WHERE id=2",expect=[('[4,5,6]',)])
t("wrong dims rejected","INSERT INTO vt VALUES (9,'x','[1,2]')",err=True)
t("L2 knn","SELECT TOP 2 id FROM vt ORDER BY l2_distance(v,'[1,2,3]')",expect=[(1,),(4,)])
t("cosine via vector_distance","SELECT TOP 1 id FROM vt ORDER BY vector_distance('cosine',v,'[0,1,0]')",expect=[(4,)])
t("string parameter","SELECT TOP 1 id FROM vt ORDER BY l2_distance(v,%s)",("[4,5,6]",),expect=[(2,)])
t("inner product / dims / norm","SELECT vector_dims(v) FROM vt WHERE id=3",expect=[(3,)])
t("avg aggregate","SELECT CAST(avg(v) AS nvarchar(50)) FROM vt WHERE id IN (1,2)",expect=[('[2.5,3.5,4.5]',)]) 
t("update","UPDATE vt SET v='[7,7,7]' WHERE id=1")
t("hnsw index","CREATE INDEX ix_vt ON vt USING hnsw (v vector_cosine_ops)")
t("filtered knn + join","SELECT TOP 1 a.tag FROM vt a JOIN vt b ON b.id=2 WHERE a.id<>2 ORDER BY vector_distance('euclidean',a.v,b.v)",expect=[('a',)])
t("delete","DELETE FROM vt WHERE id=4")
t("transaction rollback","BEGIN TRAN; INSERT INTO vt VALUES (5,'e','[1,1,1]'); ROLLBACK; SELECT COUNT(*) FROM vt",expect=[(3,)])
print(ok,"passed",bad,"failed"); sys.exit(bad)
